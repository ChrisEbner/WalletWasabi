using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using WalletWasabi.Fluent.ViewModels.Wallets.HardwareWallet;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.Tiles;
using WalletWasabi.Fluent.ViewModels.Wallets.WatchOnlyWallet;
using WalletWasabi.Gui;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets
{
	public partial class WalletViewModel : WalletViewModelBase
	{
		protected WalletViewModel(UiConfig uiConfig, Wallet wallet) : base(wallet)
		{
			Disposables = Disposables is null
				? new CompositeDisposable()
				: throw new NotSupportedException($"Cannot open {GetType().Name} before closing it.");

			var balanceChanged =
				Observable.FromEventPattern(Wallet.TransactionProcessor, nameof(Wallet.TransactionProcessor.WalletRelevantTransactionProcessed)).Select(_ => Unit.Default)
					.Throttle(TimeSpan.FromSeconds(0.1))
					.Merge(Observable.FromEventPattern(Wallet, nameof(Wallet.NewFilterProcessed)).Select(_ => Unit.Default))
					.Merge(uiConfig.WhenAnyValue(x => x.PrivacyMode).Select(_ => Unit.Default))
					.Merge(Wallet.Synchronizer.WhenAnyValue(x => x.UsdExchangeRate).Select(_ => Unit.Default))
					.ObserveOn(RxApp.MainThreadScheduler);

			History = new HistoryViewModel(wallet, uiConfig, balanceChanged);
			BalanceTile = new WalletBalanceTileViewModel(wallet, balanceChanged);
			BalanceChartTile = new WalletBalanceChartTileViewModel(History.Transactions);
			WalletPieChart = new WalletPieChartTileViewModel(wallet, balanceChanged);
		}

		private CompositeDisposable Disposables { get; set; }

		public override string IconName => "web_asset_regular";

		public HistoryViewModel History { get; }

		public WalletBalanceTileViewModel BalanceTile { get; }

		public WalletBalanceChartTileViewModel BalanceChartTile { get; }

		public WalletPieChartTileViewModel WalletPieChart { get; }

		public static WalletViewModel Create(UiConfig uiConfig, Wallet wallet)
		{
			return wallet.KeyManager.IsHardwareWallet
				? new HardwareWalletViewModel(uiConfig, wallet)
				: wallet.KeyManager.IsWatchOnly
					? new WatchOnlyWalletViewModel(uiConfig, wallet)
					: new WalletViewModel(uiConfig, wallet);
		}
	}
}
