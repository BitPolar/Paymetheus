﻿// Copyright (c) 2016-2017 The Decred developers
// Licensed under the ISC license.  See LICENSE file in the project root for full license information.

using Paymetheus.Decred;
using Paymetheus.Decred.Wallet;
using Paymetheus.Framework;
using Paymetheus.StakePoolIntegration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using Paymetheus.Decred.Util;
using IniParser;
using IniParser.Model;
using Paymetheus.Rpc;

namespace Paymetheus.ViewModels
{
    public interface IStakePoolSelection
    {
        string DisplayName { get; }
    }

    public class NoStakePool : IStakePoolSelection
    {
        public string DisplayName => "None";
    }

    public class ManualStakePool : IStakePoolSelection
    {
        public string DisplayName => "Manual entry";
    }

    public class StakePoolSelection : IStakePoolSelection
    {
        public StakePoolInfo PoolInfo { get; }
        public string ApiToken { get; }
        public string DisplayName => PoolInfo.Uri.Host;
        public byte[] MultisigVoteScript { get; }

        public StakePoolSelection(StakePoolInfo poolInfo, string apiToken, byte[] multisigVoteScript)
        {
            if (poolInfo == null) throw new ArgumentNullException(nameof(poolInfo));
            if (apiToken == null) throw new ArgumentNullException(nameof(apiToken));
            if (multisigVoteScript == null) throw new ArgumentNullException(nameof(multisigVoteScript));

            PoolInfo = poolInfo;
            ApiToken = apiToken;
            MultisigVoteScript = multisigVoteScript;
        }
    }

    class PurchaseTicketsViewModel : ViewModelBase, IActivity
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private JsonSerializer _jsonSerializer = new JsonSerializer();
        string _configPath = Path.Combine(App.Current.AppDataDir, "stakepoolcfg.json");

        public List<StakePoolInfo> AvailablePools { get; } = new List<StakePoolInfo>();

        public PurchaseTicketsViewModel() : base()
        {
            var synchronizer = ViewModelLocator.SynchronizerViewModel as SynchronizerViewModel;
            if (synchronizer != null)
            {
                SelectedSourceAccount = synchronizer.Accounts[0];
            }

            ConfiguredStakePools = new ObservableCollection<IStakePoolSelection>(new List<IStakePoolSelection>
            {
                new NoStakePool(),
                new ManualStakePool(),
            });
            _selectedStakePool = ConfiguredStakePools[0];

            ManageStakePools = new DelegateCommandAsync(ManageStakePoolsActionAsync);
            ManageStakePools.Executable = false; // Set true after pool listing is downloaded and configs are read.

            FetchStakeDifficultyCommand = new DelegateCommand(FetchStakeDifficultyAsync);
            FetchStakeDifficultyCommand.Execute(null);

            _purchaseTickets = new DelegateCommand(PurchaseTicketsAction);
            _purchaseTickets.Executable = false;

            ToggleAutoBuyerCommand = new DelegateCommandAsync(ToggleAutoBuyerAction);
            AutoBuyerEnabled = App.Current.AutoBuyerEnabled;
        }

        public async Task RunActivityAsync()
        {
            var poolListing = await PoolListApi.QueryStakePoolInfoAsync(_httpClient, _jsonSerializer);
            AvailablePools.AddRange(poolListing
                .Select(p => p.Value)
                .Where(p => p.ApiEnabled)
                .Where(p => p.Uri.Scheme == "https")
                .Where(p => p.Network == App.Current.ActiveNetwork.Name)
                .Where(p => p.SupportedApiVersions.Contains(PoolApiClient.Version))
                .OrderBy(p => p.Uri.Host));

            if (File.Exists(_configPath))
            {
                var config = await ReadConfig(_configPath);
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var entry in config.Entries)
                    {
                        var entryInfo = AvailablePools.Where(p => p.Uri.Host == entry.Host).FirstOrDefault();
                        if (entryInfo == null)
                        {
                            continue;
                        }
                        var stakePoolSelection = new StakePoolSelection(entryInfo, entry.ApiKey, Hexadecimal.Decode(entry.MultisigVoteScript));
                        ConfiguredStakePools.Add(stakePoolSelection);

                        // If only one pool is saved, use this as the default.
                        if (config.Entries.Length == 1)
                        {
                            SelectedStakePool = stakePoolSelection;
                        }
                    }
                });
            }

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                ManageStakePools.Executable = true;
                CommandManager.InvalidateRequerySuggested();
            });
        }

        async Task<StakePoolUserConfig> ReadConfig(string configPath)
        {
            using (var sr = new StreamReader(configPath, Encoding.UTF8))
            {
                return await StakePoolUserConfig.ReadConfig(_jsonSerializer, sr);
            }
        }

        private AccountViewModel _selectedSourceAccount;
        public AccountViewModel SelectedSourceAccount
        {
            get { return _selectedSourceAccount; }
            set { _selectedSourceAccount = value; RaisePropertyChanged(); }
        }

        public DelegateCommandAsync ManageStakePools { get; }
        private async Task ManageStakePoolsActionAsync()
        {
            // Open dialog that downloads stakepool listing and lets user enter their api key.
            var shell = (ShellViewModel)ViewModelLocator.ShellViewModel;
            var dialog = new ManageStakePoolsDialogViewModel(shell);
            shell.ShowDialog(dialog);
            await dialog.NotifyCompletionSource.Task; // Wait until dialog is hidden

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var configuredPool in dialog.ConfiguredPools)
                {
                    var poolInfo = configuredPool.Item1;
                    var poolUserConfig = configuredPool.Item2;

                    if (ConfiguredStakePools.OfType<StakePoolSelection>().Where(p => p.PoolInfo.Uri.Host == poolInfo.Uri.Host).Count() == 0)
                    {
                        var stakePoolSelection = new StakePoolSelection(poolInfo, poolUserConfig.ApiKey,
                            Hexadecimal.Decode(poolUserConfig.MultisigVoteScript));
                        ConfiguredStakePools.Add(stakePoolSelection);
                    }
                }
            });
        }

        public ObservableCollection<IStakePoolSelection> ConfiguredStakePools { get; }

        private Visibility _votingAddressOptionVisibility = Visibility.Visible;
        public Visibility VotingAddressOptionVisibility
        {
            get { return _votingAddressOptionVisibility; }
            set { _votingAddressOptionVisibility = value; RaisePropertyChanged(); }
        }

        private Visibility _manualPoolOptionsVisibility = Visibility.Collapsed;
        public Visibility ManualPoolOptionsVisibility
        {
            get { return _manualPoolOptionsVisibility; }
            set { _manualPoolOptionsVisibility = value; RaisePropertyChanged(); }
        }

        private IStakePoolSelection _selectedStakePool;
        public IStakePoolSelection SelectedStakePool
        {
            get { return _selectedStakePool; }
            set
            {
                _selectedStakePool = value;
                RaisePropertyChanged();

                if (value is NoStakePool)
                {
                    VotingAddressOptionVisibility = Visibility.Visible;
                    ManualPoolOptionsVisibility = Visibility.Collapsed;
                }
                else if (value is ManualStakePool)
                {
                    VotingAddressOptionVisibility = Visibility.Visible;
                    ManualPoolOptionsVisibility = Visibility.Visible;
                }
                else if (value is StakePoolSelection)
                {
                    VotingAddressOptionVisibility = Visibility.Collapsed;
                    ManualPoolOptionsVisibility = Visibility.Collapsed;
                }

                EnableOrDisableSendCommand();
            }
        }

        private uint _ticketsToPurchase = 1;
        public uint TicketsToPurchase
        {
            get { return _ticketsToPurchase; }
            set { _ticketsToPurchase = value; EnableOrDisableSendCommand(); }
        }

        private const long minFeePerKb = (long)1e6;
        private const long maxFeePerKb = (long)1e8 - 1;

        private Amount _ticketFee = minFeePerKb;
        public string TicketFee
        {
            get { return _ticketFee.ToString(); }
            set
            {
                try
                {
                    var ticketFee = Denomination.Decred.AmountFromString(value);

                    if (ticketFee < minFeePerKb)
                        throw new ArgumentException($"Too small fee passed (must be >= {(Amount)minFeePerKb} DCR/kB)");
                    if (ticketFee > maxFeePerKb)
                        throw new ArgumentException($"Too big fee passed (must be <= {(Amount)minFeePerKb} DCR/kB)");

                    _ticketFee = ticketFee;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }


        private Amount _splitFee = minFeePerKb;
        public string SplitFee
        {
            get { return _splitFee.ToString(); }
            set
            {
                try
                {
                    var splitFee = Denomination.Decred.AmountFromString(value);

                    if (splitFee < minFeePerKb)
                        throw new ArgumentException($"Too small fee passed (must be >= {(Amount)minFeePerKb} DCR/kB)");
                    if (splitFee > maxFeePerKb)
                        throw new ArgumentException($"Too big fee passed (must be <= {(Amount)minFeePerKb} DCR/kB)");

                    _splitFee = splitFee;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        private const uint MinExpiry = 2;
        private uint _expiry = 16; // The default expiry is 16.
        public uint Expiry
        {
            get { return _expiry; }
            set
            {
                try
                {
                    if (value < MinExpiry)
                        throw new ArgumentException($"Expiry must be a minimum of {MinExpiry} blocks");

                    _expiry = value;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private Address _votingAddress;
        public string VotingAddress
        {
            get { return _votingAddress?.Encode() ?? ""; }
            set
            {
                try
                {
                    _votingAddress = Address.Decode(value);
                }
                catch
                {
                    _votingAddress = null;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private Address _poolFeeAddress;
        public string PoolFeeAddress
        {
            get { return _poolFeeAddress?.Encode() ?? ""; }
            set
            {
                try
                {
                    _poolFeeAddress = Address.Decode(value);
                }
                catch
                {
                    _poolFeeAddress = null;
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        // manual
        private decimal _poolFees = 0.0m; // Percentage between 0-100 for display
        public decimal PoolFees
        {
            get { return _poolFees; }
            set
            {
                try
                {
                    _poolFees = value;
                    if (value * 100m != Math.Floor(value * 100m))
                        throw new ArgumentException("pool fees must have two decimal points of precision maximum");
                    if (value > 100.0m)
                        throw new ArgumentException("pool fees must be less or equal too than 100.00%");
                    if (value < 0.01m)
                        throw new ArgumentException("pool fees must be greater than or equal to 0.01%");
                }
                finally
                {
                    EnableOrDisableSendCommand();
                }
            }
        }

        private void EnableOrDisableSendCommand()
        {
            if (_selectedSourceAccount == null)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (_expiry < MinExpiry)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (_ticketsToPurchase <= 0)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (SelectedStakePool is ManualStakePool &&
                (_poolFeeAddress == null || _poolFees * 100m != Math.Floor(_poolFees * 100m) || _poolFees < 0.01m || _poolFees > 100m))
            {
                _purchaseTickets.Executable = false;
                return;
            }

            if (!(SelectedStakePool is StakePoolSelection) && _votingAddress == null)
            {
                _purchaseTickets.Executable = false;
                return;
            }

            // Not enough funds.
            //if ((_stakeDifficultyProperties.NextTicketPrice * (Amount)_ticketsToPurchase) > _selectedSourceAccount.Balances.SpendableBalance)
            //{
            //    // TODO: Better inform the user somehow of why it doesn't allow ticket 
            //    // purchase?
            //    //
            //    // string errorString = "Not enough funds; have " +
            //    //     _selectedAccount.Balances.SpendableBalance.ToString() + " want " +
            //    //     ((Amount)(_stakeDifficultyProperties.NextTicketPrice * (Amount)_ticketsToPurchase)).ToString();
            //    // MessageBox.Show(errorString);
            //    _purchaseTickets.Executable = false;
            //    return
            //}

            _purchaseTickets.Executable = true;
        }

        private StakeDifficultyProperties _stakeDifficultyProperties;
        public StakeDifficultyProperties StakeDifficultyProperties
        {
            get { return _stakeDifficultyProperties; }
            internal set { _stakeDifficultyProperties = value; RaisePropertyChanged(); }
        }

        public ICommand FetchStakeDifficultyCommand { get; }

        private async void FetchStakeDifficultyAsync()
        {
            try
            {
                StakeDifficultyProperties = await App.Current.Synchronizer.WalletRpcClient.StakeDifficultyAsync();
                int windowSize = 144;
                int heightOfChange = ((StakeDifficultyProperties.HeightForTicketPrice / windowSize) + 1) * windowSize;
                BlocksToRetarget = heightOfChange - StakeDifficultyProperties.HeightForTicketPrice;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error");
            }
        }

        private int _blocksToRetarget;
        public int BlocksToRetarget
        {
            get { return _blocksToRetarget; }
            internal set { _blocksToRetarget = value; RaisePropertyChanged(); }
        }


        private DelegateCommand _purchaseTickets;
        public ICommand Execute => _purchaseTickets;

        private void PurchaseTicketsAction()
        {
            var shell = ViewModelLocator.ShellViewModel as ShellViewModel;
            if (shell != null)
            {
                Func<string, Task<bool>> action =
                    passphrase => PurchaseTicketsWithPassphrase(passphrase);
                shell.VisibleDialogContent = new PassphraseDialogViewModel(shell,
                    "Enter passphrase to purchase tickets",
                    "PURCHASE",
                    action);
            }
        }

        private string _responseString = "";
        public string ResponseString
        {
            get { return _responseString; }
            set { _responseString = value; RaisePropertyChanged(); }
        }

        private async Task<bool> PurchaseTicketsWithPassphrase(string passphrase)
        {
            var walletClient = App.Current.Synchronizer.WalletRpcClient;

            var account = SelectedSourceAccount.Account;
            var spendLimit = StakeDifficultyProperties.NextTicketPrice;
            int requiredConfirms = 2; // TODO allow user to set
            uint expiryHeight = _expiry + (uint)StakeDifficultyProperties.HeightForTicketPrice;

            Amount splitFeeLocal = _splitFee;
            Amount ticketFeeLocal = _ticketFee;

            Address votingAddress;
            Address poolFeeAddress;
            decimal poolFees;
            if (SelectedStakePool is StakePoolSelection)
            {
                var selection = (StakePoolSelection)SelectedStakePool;
                var client = new PoolApiClient(selection.PoolInfo.Uri, selection.ApiToken, _httpClient);
                var purchaseInfo = await client.GetPurchaseInfoAsync();

                // Import the 1-of-2 multisig vote script.  This has to be done here rather than from
                // the pool management dialog since importing requires an unlocked wallet and we are
                // unable to open nested dialog windows to prompt for a passphrase.
                //
                // This does not need to re-import the script every time ticktes are purchased using
                // a pool, but for code simplicity it is done this way.  Also, in future versions of the
                // API when it may be possible to generate a new reward address for each ticket, we will
                // need to import these scripts ever time.
                await walletClient.ImportScriptAsync(selection.MultisigVoteScript, false, 0, passphrase);

                votingAddress = Address.Decode(purchaseInfo.VotingAddress);
                poolFeeAddress = Address.Decode(purchaseInfo.FeeAddress);
                poolFees = purchaseInfo.Fee;
            }
            else
            {
                votingAddress = _votingAddress;
                poolFeeAddress = _poolFeeAddress;
                poolFees = _poolFees / 100m;
            }

            List<Blake256Hash> purchaseResponse;
            try
            {
                purchaseResponse = await walletClient.PurchaseTicketsAsync(account, spendLimit,
                    requiredConfirms, votingAddress, _ticketsToPurchase, poolFeeAddress,
                    poolFees, expiryHeight, _splitFee, _ticketFee, passphrase);
            }
            catch (Grpc.Core.RpcException ex)
            {
                MessageBox.Show(ex.Status.Detail, "Unexpected error");
                return false;
            }

            ResponseString = "Success! Ticket hashes:\n" + string.Join("\n", purchaseResponse);
            return true;
        }

        private bool _autoBuyerEnabled;
        public bool AutoBuyerEnabled
        {
            get { return _autoBuyerEnabled; }
            set { _autoBuyerEnabled = value; ToggleAutoBuyerAction(); }
        }

        public DelegateCommandAsync ToggleAutoBuyerCommand { get; set; }
        private Task ToggleAutoBuyerAction() {
            return Task.Run(() =>
            {
                var iniParser = new FileIniDataParser();
                IniData config = null;
                var appDataDir = Portability.LocalAppData(Environment.OSVersion.Platform, AssemblyResources.Organization, AssemblyResources.ProductName);
                string defaultsFile = Path.Combine(appDataDir, "defaults.ini");
                if (File.Exists(defaultsFile))
                {
                    config = iniParser.ReadFile(defaultsFile);
                }

                if (config != null)
                {
                    var section = config["Application Options"];
                    if (section == null)
                        section = config.Global;

                    if (_autoBuyerEnabled) { section["enableticketbuyer"] = "1"; } else { section.RemoveKey("enableticketbuyer"); }
                    var parser = new FileIniDataParser();
                    parser.WriteFile(Path.Combine(appDataDir, "defaults.ini"), config);
                }

                var autoBuyerProperties = new AutoBuyerProperties {
                    Passphrase = Encoding.UTF8.GetBytes(App.Current?.PrivatePassphrase ?? ""),
                    VotingAddress = _votingAddress?.ToString() ?? "",
                    PoolAddress = _poolFeeAddress?.ToString() ?? "",
                    PoolFees = (double) _poolFees,
                    MaxFeePerKb = maxFeePerKb,
                };
                var autoBuyerViewModel = new AutoBuyerViewModel(autoBuyerProperties);

                if (_autoBuyerEnabled)
                {
                    if (autoBuyerViewModel.StartAutoBuyerCommand.CanExecute(null))
                    {
                        autoBuyerViewModel.StartAutoBuyerCommand.Execute(null);
                    }
                }
                else
                {
                    if (autoBuyerViewModel.StopAutoBuyerCommand.CanExecute(null))
                    {
                        autoBuyerViewModel.StopAutoBuyerCommand.Execute(null);
                    }
                }
            });
        }
    }
}
