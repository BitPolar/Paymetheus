﻿// Copyright (c) 2016 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Paymetheus.Decred;
using Paymetheus.Decred.Util;
using Paymetheus.Decred.Wallet;
using Paymetheus.Framework;
using Paymetheus.Rpc;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;

namespace Paymetheus.ViewModels
{
    public sealed class SynchronizerViewModel : ViewModelBase
    {
        private SynchronizerViewModel(Process walletProcess, WalletClient client)
        {
            WalletRpcProcess = walletProcess;
            WalletRpcClient = client;

            Messenger.RegisterSingleton<SynchronizerViewModel>(OnMessageReceived);
        }

        public Process WalletRpcProcess { get; }
        public WalletClient WalletRpcClient { get; }
        public Mutex<Wallet> WalletMutex { get; private set; }

        public static async Task<SynchronizerViewModel> Startup(BlockChainIdentity activeNetwork, string walletAppDataDir,
            bool searchPath, string extraArgs)
        {
            if (activeNetwork == null)
                throw new ArgumentNullException(nameof(activeNetwork));
            if (walletAppDataDir == null)
                throw new ArgumentNullException(nameof(walletAppDataDir));
            if (extraArgs == null)
                throw new ArgumentNullException(nameof(extraArgs));

            // Begin the asynchronous reading of the certificate before starting the wallet
            // process.  This uses filesystem events to know when to begin reading the certificate,
            // and if there is too much delay between wallet writing the cert and this process
            // beginning to observe the change, the event may never fire and the cert won't be read.
            var rootCertificateTask = TransportSecurity.ReadModifiedCertificateAsync(walletAppDataDir);

            string walletProcessPath = null;
            if (!searchPath)
            {
                walletProcessPath = Portability.ExecutableInstallationPath(
                    Environment.OSVersion.Platform, AssemblyResources.Organization, WalletProcess.ProcessName);
            }
            KillLeftoverWalletProcess(activeNetwork);
            var walletProcess = WalletProcess.Start(activeNetwork, walletAppDataDir, walletProcessPath, extraArgs);

            WalletClient walletClient;
            try
            {
                var listenAddress = WalletProcess.RpcListenAddress("localhost", activeNetwork);
                var rootCertificate = await rootCertificateTask;
                walletClient = await WalletClient.ConnectAsync(listenAddress, rootCertificate);
            }
            catch (Exception)
            {
                if (walletProcess.HasExited)
                {
                    throw new Exception("Wallet process closed unexpectedly");
                }
                walletProcess.KillIfExecuting();
                throw;
            }

            return new SynchronizerViewModel(walletProcess, walletClient);
        }

        private static void KillLeftoverWalletProcess(BlockChainIdentity intendedNetwork)
        {
            var v4ListenAddress = WalletProcess.RpcListenAddress("127.0.0.1", intendedNetwork);
            var walletProcesses = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE Name='{WalletProcess.ProcessName}.exe'").Get();
            foreach (var walletProcessInfo in walletProcesses)
            {
                var commandLine = (string)walletProcessInfo["CommandLine"];
                if (commandLine.Contains($" --experimentalrpclisten={v4ListenAddress}"))
                {
                    var process = Process.GetProcessById((int)(uint)walletProcessInfo["ProcessID"]);
                    process.KillIfExecuting();
                    break;
                }
            }
        }

        private Amount _totalBalance;
        public Amount TotalBalance
        {
            get { return _totalBalance; }
            set { _totalBalance = value; RaisePropertyChanged(); }
        }

        private int _transactionCount;
        public int TransactionCount
        {
            get { return _transactionCount; }
            set { _transactionCount = value; RaisePropertyChanged(); }
        }

        private int _syncedBlockHeight;
        public int SyncedBlockHeight
        {
            get { return _syncedBlockHeight; }
            set { _syncedBlockHeight = value; RaisePropertyChanged(); }
        }

        public ObservableCollection<AccountViewModel> Accounts { get; } = new ObservableCollection<AccountViewModel>();

        private AccountViewModel _selectedAccount;
        public AccountViewModel SelectedAccount
        {
            get { return _selectedAccount; }
            set { _selectedAccount = value; RaisePropertyChanged(); }
        }

        public IEnumerable<string> AccountNames => Accounts.Select(a => a.AccountProperties.AccountName);

        private async void OnMessageReceived(IViewModelMessage message)
        {
            var startupWizardFinishedMessage = message as StartupWizardFinishedMessage;
            if (startupWizardFinishedMessage != null)
            {
                await OnWalletProcessOpenedWallet();
            }
        }

        private async Task OnWalletProcessOpenedWallet()
        {
            try
            {
                var syncingWallet = await WalletRpcClient.Synchronize(OnWalletChangesProcessed);
                WalletMutex = syncingWallet.Item1;
                using (var guard = await WalletMutex.LockAsync())
                {
                    OnSyncedWallet(guard.Instance);
                }

                var syncTask = syncingWallet.Item2;
                await syncTask;
            }
            catch (ConnectTimeoutException)
            {
                MessageBox.Show("Unable to connect to wallet");
            }
            catch (Grpc.Core.RpcException) when (WalletRpcClient.CancellationRequested) { }
            catch (Exception ex)
            {
                var ae = ex as AggregateException;
                if (ae != null)
                {
                    Exception inner;
                    if (ae.TryUnwrap(out inner))
                        ex = inner;
                }

                await HandleSyncFault(ex);
            }
            finally
            {
                if (WalletMutex != null)
                {
                    using (var walletGuard = await WalletMutex.LockAsync())
                    {
                        walletGuard.Instance.ChangesProcessed -= OnWalletChangesProcessed;
                    }
                }

                var shell = (ShellViewModel)ViewModelLocator.ShellViewModel;
                shell.StartupWizardVisible = true;
            }
        }

        private static async Task HandleSyncFault(Exception ex)
        {
            string message;
            var shutdown = false;

            // Sync task ended.  Decide whether to restart the task and sync a
            // fresh wallet, or error out with an explanation.
            if (ErrorHandling.IsTransient(ex))
            {
                // This includes network issues reaching the wallet, like disconnects.
                message = $"A temporary error occurred, but reconnecting is not implemented.\n\n{ex}";
                shutdown = true; // TODO: implement reconnect logic.
            }
            else if (ErrorHandling.IsServerError(ex))
            {
                message = $"The wallet failed to service a request.\n\n{ex}";
            }
            else if (ErrorHandling.IsClientError(ex))
            {
                message = $"A client request could not be serviced.\n\n{ex}";
            }
            else
            {
                message = $"An unexpected error occurred:\n\n{ex}";
                shutdown = true;
            }

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, "Error");
                if (shutdown)
                    App.Current.Shutdown();
            });
        }

        private void OnWalletChangesProcessed(object sender, Wallet.ChangesProcessedEventArgs e)
        {
            var wallet = (Wallet)sender;
            var currentHeight = e.NewChainTip?.Height ?? SyncedBlockHeight;

            // TODO: The OverviewViewModel should probably connect to this event.  This could be
            // done after the wallet is synced.
            var overviewViewModel = ViewModelLocator.OverviewViewModel as OverviewViewModel;
            if (overviewViewModel != null)
            {
                var movedTxViewModels = overviewViewModel.RecentTransactions
                    .Where(txvm => e.MovedTransactions.ContainsKey(txvm.TxHash))
                    .Select(txvm => Tuple.Create(txvm, e.MovedTransactions[txvm.TxHash]));

                var newTxViewModels = e.AddedTransactions.Select(tx => new TransactionViewModel(wallet, tx.Item1, tx.Item2)).ToList();

                foreach (var movedTx in movedTxViewModels)
                {
                    var txvm = movedTx.Item1;
                    var location = movedTx.Item2;

                    txvm.Location = location;
                    txvm.Depth = BlockChain.Depth(currentHeight, location);
                }

                App.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var txvm in newTxViewModels)
                    {
                        overviewViewModel.RecentTransactions.Insert(0, txvm);
                    }
                    while (overviewViewModel.RecentTransactions.Count > 10)
                    {
                        overviewViewModel.RecentTransactions.RemoveAt(10);
                    }
                });
            }

            // TODO: same.. in fact these tx viewmodels should be reused so changes don't need to be recalculated.
            // It would be a good idea for this synchronzier viewmodel to manage these and hand them out to other
            // viewmodels for sorting and organization.
            var transactionHistoryViewModel = ViewModelLocator.TransactionHistoryViewModel as TransactionHistoryViewModel;
            if (transactionHistoryViewModel != null)
            {
                foreach (var tx in transactionHistoryViewModel.Transactions)
                {
                    var txvm = tx.Transaction;
                    BlockIdentity newLocation;
                    if (e.MovedTransactions.TryGetValue(txvm.TxHash, out newLocation))
                    {
                        txvm.Location = newLocation;
                    }
                    txvm.Depth = BlockChain.Depth(currentHeight, txvm.Location);
                }

                transactionHistoryViewModel.AppendNewTransactions(wallet, e.AddedTransactions);
            }

            foreach (var modifiedAccount in e.ModifiedAccountProperties)
            {
                var accountNumber = checked((int)modifiedAccount.Key.AccountNumber);
                var accountProperties = modifiedAccount.Value;

                if (accountNumber < Accounts.Count)
                {
                    Accounts[accountNumber].AccountProperties = accountProperties;
                }
            }

            // TODO: this would be better if all new accounts were a field in the event message.
            var newAccounts = e.ModifiedAccountProperties.
                Where(kvp => kvp.Key.AccountNumber >= Accounts.Count && kvp.Key.AccountNumber != Wallet.ImportedAccountNumber).
                OrderBy(kvp => kvp.Key.AccountNumber);
            foreach (var modifiedAccount in newAccounts)
            {
                var accountNumber = checked((int)modifiedAccount.Key.AccountNumber);
                var accountProperties = modifiedAccount.Value;

                // TODO: This is very inefficient because it recalculates balances of every account, for each new account.
                var accountBalance = wallet.CalculateBalances(2)[accountNumber];
                var accountViewModel = new AccountViewModel(modifiedAccount.Key, accountProperties, accountBalance);
                App.Current.Dispatcher.Invoke(() => Accounts.Add(accountViewModel));
            }

            if (e.NewChainTip != null)
            {
                SyncedBlockHeight = e.NewChainTip.Value.Height;
            }
            if (e.AddedTransactions.Count != 0 || e.RemovedTransactions.Count != 0 || e.NewChainTip != null)
            {
                TotalBalance = wallet.TotalBalance;
                TransactionCount += e.AddedTransactions.Count - e.RemovedTransactions.Count;
                var balances = wallet.CalculateBalances(2); // TODO: don't hardcode confs
                for (var i = 0; i < balances.Length; i++)
                {
                    Accounts[i].Balances = balances[i];
                }
            }
        }

        private void OnSyncedWallet(Wallet wallet)
        {
            var accountBalances = wallet.CalculateBalances(2); // TODO: configurable confirmations
            var accountViewModels = wallet.EnumerateAccounts()
                .Zip(accountBalances, (a, bals) => new AccountViewModel(a.Item1, a.Item2, bals))
                .ToList();

            var txSet = wallet.RecentTransactions;
            var recentTx = txSet.UnminedTransactions
                .Select(x => new TransactionViewModel(wallet, x.Value, BlockIdentity.Unmined))
                .Concat(txSet.MinedTransactions.ReverseList().SelectMany(b => b.Transactions.Select(tx => new TransactionViewModel(wallet, tx, b.Identity))))
                .Take(10);
            var overviewViewModel = (OverviewViewModel)SingletonViewModelLocator.Resolve("Overview");

            App.Current.Dispatcher.Invoke(() =>
            {
                foreach (var vm in accountViewModels)
                    Accounts.Add(vm);
                foreach (var tx in recentTx)
                    overviewViewModel.RecentTransactions.Add(tx);
            });
            TotalBalance = wallet.TotalBalance;
            TransactionCount = txSet.TransactionCount();
            SyncedBlockHeight = wallet.ChainTip.Height;
            SelectedAccount = accountViewModels[0];
            RaisePropertyChanged(nameof(TotalBalance));
            RaisePropertyChanged(nameof(AccountNames));
            overviewViewModel.AccountsCount = accountViewModels.Count();

            if (App.Current.AutoBuyerProperties != null)
            {
                App.Current.Dispatcher.InvokeAsync(() =>
                    PurchaseTicketsViewModel.StartAutoBuyer(App.Current.AutoBuyerProperties.Passphrase)
                );
            }

            var shell = (ShellViewModel)ViewModelLocator.ShellViewModel;
            shell.StartupWizardVisible = false;

        }
    }
}
