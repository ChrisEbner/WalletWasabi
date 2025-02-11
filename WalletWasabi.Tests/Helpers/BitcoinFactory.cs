using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;

namespace WalletWasabi.Tests.Helpers
{
	public static class BitcoinFactory
	{
		public static SmartTransaction CreateSmartTransaction(int othersInputCount = 1, int othersOutputCount = 1, int ownInputCount = 0, int ownOutputCount = 0)
			=> CreateSmartTransaction(othersInputCount, Enumerable.Repeat(Money.Coins(1m), othersOutputCount), Enumerable.Repeat((Money.Coins(1.1m), 1), ownInputCount), Enumerable.Repeat((Money.Coins(1m), 1), ownOutputCount));

		public static SmartTransaction CreateSmartTransaction(int othersInputCount, IEnumerable<Money> othersOutputs, IEnumerable<(Money value, int anonset)> ownInputs, IEnumerable<(Money value, int anonset)> ownOutputs)
		{
			var km = ServiceFactory.CreateKeyManager();
			return CreateSmartTransaction(othersInputCount, othersOutputs, ownInputs.Select(x => (x.value, x.anonset, CreateHdPubKey(km))), ownOutputs.Select(x => (x.value, x.anonset, CreateHdPubKey(km))));
		}

		public static SmartTransaction CreateSmartTransaction(int othersInputCount, IEnumerable<Money> othersOutputs, IEnumerable<(Money value, int anonset, HdPubKey hdpk)> ownInputs, IEnumerable<(Money value, int anonset, HdPubKey hdpk)> ownOutputs)
			=> CreateSmartTransaction(othersInputCount, othersOutputs.Select(x => new TxOut(x, new Key())), ownInputs, ownOutputs);

		public static SmartTransaction CreateSmartTransaction(int othersInputCount, IEnumerable<TxOut> othersOutputs, IEnumerable<(Money value, int anonset, HdPubKey hdpk)> ownInputs, IEnumerable<(Money value, int anonset, HdPubKey hdpk)> ownOutputs)
		{
			var tx = Transaction.Create(Network.Main);
			var walletInputs = new HashSet<SmartCoin>();
			var walletOutputs = new HashSet<SmartCoin>();

			for (int i = 0; i < othersInputCount; i++)
			{
				tx.Inputs.Add(CreateOutPoint());
			}
			var idx = (uint)othersInputCount - 1;
			foreach (var (value, anonset, hdpk) in ownInputs)
			{
				idx++;
				var sc = CreateSmartCoin(hdpk, value, idx, anonymitySet: anonset);
				tx.Inputs.Add(sc.OutPoint);
				walletInputs.Add(sc);
			}

			foreach (var output in othersOutputs)
			{
				tx.Outputs.Add(output);
			}
			idx = (uint)othersOutputs.Count() - 1;
			foreach (var txo in ownOutputs)
			{
				idx++;
				var hdpk = txo.hdpk;
				tx.Outputs.Add(new TxOut(txo.value, hdpk.P2wpkhScript));
				var sc = new SmartCoin(new SmartTransaction(tx, Height.Mempool), idx, hdpk);
				walletOutputs.Add(sc);
			}

			var stx = new SmartTransaction(tx, Height.Mempool);

			foreach (var sc in walletInputs)
			{
				stx.WalletInputs.Add(sc);
			}

			foreach (var sc in walletOutputs)
			{
				stx.WalletOutputs.Add(sc);
			}

			return stx;
		}

		public static HdPubKey CreateHdPubKey(KeyManager km)
			=> km.GenerateNewKey(SmartLabel.Empty, KeyState.Clean, isInternal: false);

		public static SmartCoin CreateSmartCoin(HdPubKey pubKey, decimal amountBtc, uint index = 0, bool confirmed = true, int anonymitySet = 1)
			=> CreateSmartCoin(pubKey, Money.Coins(amountBtc), index, confirmed, anonymitySet);

		public static SmartCoin CreateSmartCoin(HdPubKey pubKey, Money amount, uint index = 0, bool confirmed = true, int anonymitySet = 1)
		{
			var height = confirmed ? new Height(CryptoHelpers.RandomInt(0, 200)) : Height.Mempool;
			pubKey.SetKeyState(KeyState.Used);
			var tx = Transaction.Create(Network.Main);
			tx.Outputs.Add(new TxOut(amount, pubKey.P2wpkhScript));
			var stx = new SmartTransaction(tx, height);
			pubKey.SetAnonymitySet(anonymitySet, stx.GetHash());
			return new SmartCoin(stx, index, pubKey);
		}

		public static OutPoint CreateOutPoint()
			=> new(CreateUint256(), (uint)CryptoHelpers.RandomInt(0, 100));

		public static uint256 CreateUint256()
		{
			var rand = new UnsecureRandom();
			var bytes = new byte[32];
			rand.GetBytes(bytes);
			return new uint256(bytes);
		}

		public static Script CreateScript(Key? key = null)
		{
			if (key is null)
			{
				using Key k = new();
				return k.PubKey.WitHash.ScriptPubKey;
			}
			else
			{
				return key.PubKey.WitHash.ScriptPubKey;
			}
		}
	}
}
