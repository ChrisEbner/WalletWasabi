using NBitcoin;
using NBitcoin.RPC;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Backend.Banning;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Crypto.CredentialRequesting;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Rounds
{
	public class Arena : PeriodicRunner
	{
		public Arena(TimeSpan period, Network network, WabiSabiConfig config, IRPCClient rpc, Prison prison) : base(period)
		{
			Network = network;
			Config = config;
			Rpc = rpc;
			Prison = prison;
			Random = new SecureRandom();
		}

		public Dictionary<Guid, Round> Rounds { get; } = new();
		private AsyncLock AsyncLock { get; } = new();
		public Network Network { get; }
		public WabiSabiConfig Config { get; }
		public IRPCClient Rpc { get; }
		public Prison Prison { get; }
		public SecureRandom Random { get; }

		protected override async Task ActionAsync(CancellationToken cancel)
		{
			using (await AsyncLock.LockAsync(cancel).ConfigureAwait(false))
			{
				// Remove timed out alices.
				TimeoutAlices();

				await StepTransactionSigningPhaseAsync().ConfigureAwait(false);

				StepOutputRegistrationPhase();

				StepConnectionConfirmationPhase();

				StepInputRegistrationPhase();

				cancel.ThrowIfCancellationRequested();

				// Ensure there's at least one non-blame round in input registration.
				await CreateRoundsAsync().ConfigureAwait(false);
			}
		}

		private void StepInputRegistrationPhase()
		{
			foreach (var round in Rounds.Values.Where(x =>
				x.Phase == Phase.InputRegistration
				&& x.IsInputRegistrationEnded(Config.MaxInputCountByRound, Config.GetInputRegistrationTimeout(x)))
				.ToArray())
			{
				if (round.InputCount < Config.MinInputCountByRound)
				{
					Rounds.Remove(round.Id);
					round.LogInfo($"Not enough inputs ({round.InputCount}) in {nameof(Phase.InputRegistration)} phase.");
				}
				else
				{
					round.SetPhase(Phase.ConnectionConfirmation);
				}
			}
		}

		private void StepConnectionConfirmationPhase()
		{
			foreach (var round in Rounds.Values.Where(x => x.Phase == Phase.ConnectionConfirmation).ToArray())
			{
				if (round.Alices.All(x => x.ConfirmedConnection))
				{
					round.SetPhase(Phase.OutputRegistration);
				}
				else if (round.ConnectionConfirmationStart + round.ConnectionConfirmationTimeout < DateTimeOffset.UtcNow)
				{
					var alicesDidntConfirm = round.Alices.Where(x => !x.ConfirmedConnection).ToArray();
					foreach (var alice in alicesDidntConfirm)
					{
						Prison.Note(alice, round.Id);
					}
					var removedAliceCount = round.Alices.RemoveAll(x => alicesDidntConfirm.Contains(x));
					round.LogInfo($"{removedAliceCount} alices removed because they didn't confirm.");

					if (round.InputCount < Config.MinInputCountByRound)
					{
						Rounds.Remove(round.Id);
						round.LogInfo($"Not enough inputs ({round.InputCount}) in {nameof(Phase.ConnectionConfirmation)} phase.");
					}
					else
					{
						round.SetPhase(Phase.OutputRegistration);
					}
				}
			}
		}

		private void StepOutputRegistrationPhase()
		{
			foreach (var round in Rounds.Values.Where(x => x.Phase == Phase.OutputRegistration).ToArray())
			{
				long aliceSum = round.Alices.Sum(x => x.CalculateRemainingAmountCredentials(round.FeeRate));
				long bobSum = round.Bobs.Sum(x => x.CredentialAmount);
				var diff = aliceSum - bobSum;
				if (diff == 0 || round.OutputRegistrationStart + round.OutputRegistrationTimeout < DateTimeOffset.UtcNow)
				{
					// Build a coinjoin:
					var coinjoin = round.Coinjoin;

					// Add inputs:
					var spentCoins = round.Alices.SelectMany(x => x.Coins).ToArray();
					foreach (var input in spentCoins.Select(x => x.Outpoint))
					{
						coinjoin.Inputs.Add(input);
					}
					round.LogInfo($"{coinjoin.Inputs.Count} inputs were added.");

					// Add outputs:
					foreach (var bob in round.Bobs)
					{
						coinjoin.Outputs.AddWithOptimize(bob.CalculateOutputAmount(round.FeeRate), bob.Script);
					}
					round.LogInfo($"{round.Bobs.Count} outputs were added.");

					// Shuffle & sort:
					// This is basically just decoration.
					coinjoin.Inputs.Shuffle();
					coinjoin.Outputs.Shuffle();
					coinjoin.Inputs.SortByAmount(spentCoins);
					coinjoin.Outputs.SortByAmount();

					// If timeout we must fill up the outputs to build a reasonable transaction.
					// This won't be signed by the alice who failed to provide output, so we know who to ban.
					if (diff > round.MinRegistrableAmount)
					{
						var diffMoney = Money.Satoshis(diff);
						coinjoin.Outputs.AddWithOptimize(diffMoney, Config.BlameScript);
						round.LogInfo("Filled up the outputs to build a reasonable transaction because some alice failed to provide its output.");
					}

					round.SetPhase(Phase.TransactionSigning);
				}
			}
		}

		private async Task StepTransactionSigningPhaseAsync()
		{
			foreach (var round in Rounds.Values.Where(x => x.Phase == Phase.TransactionSigning).ToArray())
			{
				var coinjoin = round.Coinjoin;
				var isFullySigned = coinjoin.Inputs.All(x => x.HasWitScript());

				try
				{
					if (isFullySigned)
					{
						// Logging.
						round.LogInfo("Trying to broadcast coinjoin.");
						Coin[]? spentCoins = round.Alices.SelectMany(x => x.Coins).ToArray();
						Money networkFee = coinjoin.GetFee(spentCoins);
						Guid roundId = round.Id;
						FeeRate feeRate = coinjoin.GetFeeRate(spentCoins);
						round.LogInfo($"Network Fee: {networkFee.ToString(false, false)} BTC.");
						round.LogInfo($"Network Fee Rate: {feeRate.FeePerK.ToDecimal(MoneyUnit.Satoshi) / 1000} sat/vByte.");
						round.LogInfo($"Number of inputs: {coinjoin.Inputs.Count}.");
						round.LogInfo($"Number of outputs: {coinjoin.Outputs.Count}.");
						round.LogInfo($"Serialized Size: {coinjoin.GetSerializedSize() / 1024} KB.");
						round.LogInfo($"VSize: {coinjoin.GetVirtualSize() / 1024} KB.");
						foreach (var (value, count) in coinjoin.GetIndistinguishableOutputs(includeSingle: false))
						{
							round.LogInfo($"There are {count} occurrences of {value.ToString(true, false)} BTC output.");
						}

						// Broadcasting.
						await Rpc.SendRawTransactionAsync(coinjoin).ConfigureAwait(false);
						round.SetPhase(Phase.TransactionBroadcasting);

						round.LogInfo($"Successfully broadcast the CoinJoin: {coinjoin.GetHash()}.");
					}
					else if (round.TransactionSigningStart + round.TransactionSigningTimeout < DateTimeOffset.UtcNow)
					{
						throw new TimeoutException($"Round {round.Id}: Signing phase timed out after {round.TransactionSigningTimeout.TotalSeconds} seconds.");
					}
				}
				catch (Exception ex)
				{
					round.LogWarning($"Signing phase failed, reason: '{ex}'.");
					await FailTransactionSigningPhaseAsync(round).ConfigureAwait(false);
				}
			}
		}

		private async Task FailTransactionSigningPhaseAsync(Round round)
		{
			var unsignedCoins = round.Coinjoin.Inputs.Where(x => !x.HasWitScript()).Select(x => x.PrevOut);
			var alicesWhoDidntSign = round.Alices
				.SelectMany(alice => alice.Coins, (alice, coin) => (Alice: alice, coin.Outpoint))
				.Where(x => unsignedCoins.Contains(x.Outpoint))
				.Select(x => x.Alice)
				.ToHashSet();

			foreach (var alice in alicesWhoDidntSign)
			{
				Prison.Note(alice, round.Id);
			}

			round.Alices.RemoveAll(x => alicesWhoDidntSign.Contains(x));
			Rounds.Remove(round.Id);

			if (round.InputCount >= Config.MinInputCountByRound)
			{
				await CreateBlameRoundAsync(round).ConfigureAwait(false);
			}
		}

		private async Task CreateBlameRoundAsync(Round round)
		{
			var feeRate = (await Rpc.EstimateSmartFeeAsync((int)Config.ConfirmationTarget, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true).ConfigureAwait(false)).FeeRate;
			RoundParameters parameters = new(Config, Network, Random, feeRate, blameOf: round);
			Round blameRound = new(parameters);
			Rounds.Add(blameRound.Id, blameRound);
		}

		private async Task CreateRoundsAsync()
		{
			if (!Rounds.Values.Any(x => !x.IsBlameRound && x.Phase == Phase.InputRegistration))
			{
				var feeRate = (await Rpc.EstimateSmartFeeAsync((int)Config.ConfirmationTarget, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true).ConfigureAwait(false)).FeeRate;

				RoundParameters roundParams = new(Config, Network, Random, feeRate);
				Round r = new(roundParams);
				Rounds.Add(r.Id, r);
			}
		}

		private void TimeoutAlices()
		{
			foreach (var round in Rounds.Values.Where(x => !x.IsInputRegistrationEnded(Config.MaxInputCountByRound, Config.GetInputRegistrationTimeout(x))).ToArray())
			{
				var removedAliceCount = round.Alices.RemoveAll(x => x.Deadline < DateTimeOffset.UtcNow);
				if (removedAliceCount > 0)
				{
					round.LogInfo($"{removedAliceCount} alices timed out and removed.");
				}
			}
		}

		public async Task<InputsRegistrationResponse> RegisterInputAsync(
			Guid roundId,
			IDictionary<Coin, byte[]> coinRoundSignaturePairs,
			ZeroCredentialsRequest zeroAmountCredentialRequests,
			ZeroCredentialsRequest zeroWeightCredentialRequests)
		{
			using (await AsyncLock.LockAsync().ConfigureAwait(false))
			{
				return InputRegistrationHandler.RegisterInput(
					Config,
					roundId,
					coinRoundSignaturePairs,
					zeroAmountCredentialRequests,
					zeroWeightCredentialRequests,
					Rounds,
					Network);
			}
		}

		public async Task RemoveInputAsync(InputsRemovalRequest request)
		{
			using (await AsyncLock.LockAsync().ConfigureAwait(false))
			{
				if (!Rounds.TryGetValue(request.RoundId, out var round))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({request.RoundId}) not found.");
				}
				if (round.Phase != Phase.InputRegistration)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongPhase, $"Round ({request.RoundId}): Wrong phase ({round.Phase}).");
				}
				round.Alices.RemoveAll(x => x.Id == request.AliceId);
			}
		}

		public async Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request)
		{
			using (await AsyncLock.LockAsync().ConfigureAwait(false))
			{
				if (!Rounds.TryGetValue(request.RoundId, out var round))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({request.RoundId}) not found.");
				}

				var alice = round.Alices.FirstOrDefault(x => x.Id == request.AliceId);
				if (alice is null)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceNotFound, $"Round ({request.RoundId}): Alice ({request.AliceId}) not found.");
				}

				var realAmountCredentialRequests = request.RealAmountCredentialRequests;
				var realVsizeCredentialRequests = request.RealVsizeCredentialRequests;

				if (realVsizeCredentialRequests.Delta != alice.CalculateRemainingVsizeCredentials(round.PerAliceVsizeAllocation))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested weight credentials.");
				}
				if (realAmountCredentialRequests.Delta != alice.CalculateRemainingAmountCredentials(round.FeeRate))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedAmountCredentials, $"Round ({request.RoundId}): Incorrect requested amount credentials.");
				}

				var commitAmountZeroCredentialResponse = round.AmountCredentialIssuer.PrepareResponse(request.ZeroAmountCredentialRequests);
				var commitVsizeZeroCredentialResponse = round.VsizeCredentialIssuer.PrepareResponse(request.ZeroVsizeCredentialRequests);

				if (round.Phase == Phase.InputRegistration)
				{
					alice.SetDeadlineRelativeTo(round.ConnectionConfirmationTimeout);
					return new(
						commitAmountZeroCredentialResponse.Commit(),
						commitVsizeZeroCredentialResponse.Commit());
				}
				else if (round.Phase == Phase.ConnectionConfirmation)
				{
					var commitAmountRealCredentialResponse = round.AmountCredentialIssuer.PrepareResponse(realAmountCredentialRequests);
					var commitVsizeRealCredentialResponse = round.VsizeCredentialIssuer.PrepareResponse(realVsizeCredentialRequests);
					alice.ConfirmedConnection = true;

					return new(
						commitAmountZeroCredentialResponse.Commit(),
						commitVsizeZeroCredentialResponse.Commit(),
						commitAmountRealCredentialResponse.Commit(),
						commitVsizeRealCredentialResponse.Commit());
				}
				else
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongPhase, $"Round ({request.RoundId}): Wrong phase ({round.Phase}).");
				}
			}
		}

		public async Task<OutputRegistrationResponse> RegisterOutputAsync(OutputRegistrationRequest request)
		{
			using (await AsyncLock.LockAsync().ConfigureAwait(false))
			{
				if (!Rounds.TryGetValue(request.RoundId, out var round))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({request.RoundId}) not found.");
				}

				var credentialAmount = -request.AmountCredentialRequests.Delta;

				if (!StandardScripts.IsStandardScriptPubKey(request.Script))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.NonStandardOutput, $"Round ({request.RoundId}): Non standard output.");
				}

				if (!request.Script.IsScriptType(ScriptType.P2WPKH))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.ScriptNotAllowed, $"Round ({request.RoundId}): Script not allowed.");
				}

				Bob bob = new(request.Script, credentialAmount);

				var outputValue = bob.CalculateOutputAmount(round.FeeRate);
				if (outputValue < round.MinRegistrableAmount)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.NotEnoughFunds, $"Round ({request.RoundId}): Not enough funds.");
				}
				if (outputValue > round.MaxRegistrableAmount)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.TooMuchFunds, $"Round ({request.RoundId}): Too much funds.");
				}

				var vsizeCredentialRequests = request.VsizeCredentialRequests;
				if (-vsizeCredentialRequests.Delta != bob.OutputVsize)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested weight credentials.");
				}

				if (round.Phase != Phase.OutputRegistration)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongPhase, $"Round ({request.RoundId}): Wrong phase ({round.Phase}).");
				}

				var commitAmountCredentialResponse = round.AmountCredentialIssuer.PrepareResponse(request.AmountCredentialRequests);
				var commitVsizeCredentialResponse = round.VsizeCredentialIssuer.PrepareResponse(vsizeCredentialRequests);

				round.Bobs.Add(bob);

				return new(
					commitAmountCredentialResponse.Commit(),
					commitVsizeCredentialResponse.Commit());
			}
		}

		public async Task SignTransactionAsync(TransactionSignaturesRequest request)
		{
			using (await AsyncLock.LockAsync().ConfigureAwait(false))
			{
				if (!Rounds.TryGetValue(request.RoundId, out var round))
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({request.RoundId}) not found.");
				}

				if (round.Phase != Phase.TransactionSigning)
				{
					throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongPhase, $"Round ({request.RoundId}): Wrong phase ({round.Phase}).");
				}
				foreach (var inputWitnessPair in request.InputWitnessPairs)
				{
					var index = (int)inputWitnessPair.InputIndex;
					var witness = inputWitnessPair.Witness;

					// If input is already signed, don't bother.
					if (round.Coinjoin.Inputs[index].HasWitScript())
					{
						continue;
					}

					// Verify witness.
					// 1. Copy UnsignedCoinJoin.
					Transaction cjCopy = Transaction.Parse(round.Coinjoin.ToHex(), Network);

					// 2. Sign the copy.
					cjCopy.Inputs[index].WitScript = witness;

					// 3. Convert the current input to IndexedTxIn.
					IndexedTxIn currentIndexedInput = cjCopy.Inputs.AsIndexedInputs().Skip(index).First();

					// 4. Find the corresponding registered input.
					Coin registeredCoin = round.Alices.SelectMany(x => x.Coins).Single(x => x.Outpoint == cjCopy.Inputs[index].PrevOut);

					// 5. Verify if currentIndexedInput is correctly signed, if not, return the specific error.
					if (!currentIndexedInput.VerifyScript(registeredCoin, out ScriptError error))
					{
						throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongCoinjoinSignature, $"Round ({request.RoundId}): Wrong CoinJoin signature.");
					}

					// Finally add it to our CJ.
					round.Coinjoin.Inputs[index].WitScript = witness;
				}
			}
		}

		public override void Dispose()
		{
			Random.Dispose();
			base.Dispose();
		}
	}
}
