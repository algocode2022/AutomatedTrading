﻿using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using AutomatedTraderDesigner.Services;
using Hallupa.Library;
using log4net;
using Newtonsoft.Json;
using TraderTools.Basics;
using TraderTools.Core.Services;
using TraderTools.Core.Trading;
using TraderTools.Simulation;

namespace AutomatedTraderDesigner.ViewModels
{
    public class StrategyRunViewModel : INotifyPropertyChanged
    {
        #region Fields
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        [Import] private IBrokersCandlesService _candlesService;
        [Import] private IBrokersService _brokersService;
        [Import] private StrategyRunnerResultsService _results;
        [Import] private MarketsService _marketsService;
        [Import] private StrategyService _strategyService;
        [Import] private IMarketDetailsService _marketDetailsService;
        [Import] private ITradeDetailsAutoCalculatorService _tradeCalculatorService;
        [Import] private UIService _uiService;
        [Import] private IDataDirectoryService _dataDirectoryService;

        private bool _runStrategyEnabled = true;
        private Dispatcher _dispatcher;
        private ProducerConsumer<(IStrategy Strategy, MarketDetails Market)> _producerConsumer;
        private IDisposable _strategiesUpdatedDisposable;
        private List<IStrategy> _strategies;
        private string _savedResultsPath;
        public static string CustomCode { get; set; }

        #endregion

        #region Constructors

        public StrategyRunViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            DependencyContainer.ComposeParts(this);

            Markets = new ObservableCollection<string>(_marketsService.GetMarkets().Select(m => m.Name).OrderBy(x => x));

            // Load existing results
            _savedResultsPath = Path.Combine(_dataDirectoryService.MainDirectoryWithApplicationName, "StrategyTesterResults.json");
            if (File.Exists(_savedResultsPath))
            {
                var results = JsonConvert.DeserializeObject<List<Trade>>(File.ReadAllText(_savedResultsPath));
                _results.AddResult(results);
                _results.RaiseTestRunCompleted();
            }

            RunStrategyCommand = new DelegateCommand(RunStrategyClicked);
            StrategiesUpdated(null);
            _strategiesUpdatedDisposable = StrategyService.UpdatedObservable.Subscribe(StrategiesUpdated);
            _uiService.RegisterF5Action(() => RunStrategyClicked(null));
        }

        private void StrategiesUpdated(object obj)
        {
            var selectedStrategyNames = SelectedStrategies.Cast<IStrategy>().Select(s => s.Name).ToList();

            SelectedStrategies.Clear();
            Strategies = StrategyService.Strategies.ToList();

            foreach (var selectedStrategyName in selectedStrategyNames)
            {
                var newSelectedStrategy = Strategies.FirstOrDefault(s => s.Name == selectedStrategyName);
                if (newSelectedStrategy != null && SelectedStrategies.Cast<IStrategy>().All(s => s.Name != selectedStrategyName))
                {
                    SelectedStrategies.Add(newSelectedStrategy);
                }
            }

            OnPropertyChanged("SelectedStrategies");
        }

        public List<IStrategy> Strategies
        {
            get => _strategies;
            private set
            {
                _strategies = value;
                OnPropertyChanged();
            }
        }



        #endregion

        #region Properties

        [Import]
        public StrategyService StrategyService { get; private set; }
        public ObservableCollection<string> Markets { get; private set; }
        public ICommand RunStrategyCommand { get; private set; }
        public List<object> SelectedStrategies { get; set; } = new List<object>();
        public List<object> SelectedMarkets { get; set; } = new List<object>();
        public string StartDate { get; set; }
        public string EndDate { get; set; }
        public bool UpdatePrices { get; set; }


        public bool RunStrategyEnabled
        {
            get { return _runStrategyEnabled; }
            private set
            {
                _runStrategyEnabled = value;
                NotifyPropertyChanged();
            }
        }

        #endregion

        private void RunStrategyClicked(object o)
        {
            if (SelectedMarkets.Count == 0 || SelectedStrategies.Count == 0)
            {
                return;
            }

            RunStrategyEnabled = false;

            Task.Run((Action)RunStrategy);
        }


        private void NotifyPropertyChanged([CallerMemberName]string name = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void RunStrategy()
        {
            Log.Info("Running simulation");
            var stopwatch = Stopwatch.StartNew();
            var strategies = SelectedStrategies.ToList();
            var broker = _brokersService.Brokers.First(x => x.Name == "FXCM");

            var markets = SelectedMarkets.Cast<string>().ToList();
            _results.Reset();

            _dispatcher.Invoke((Action)(() =>
            {
                _results.RaiseTestRunStarted();

                NotifyPropertyChanged("TotalTrades");
                NotifyPropertyChanged("AverageRTrade");
                NotifyPropertyChanged("TotalR");
                NotifyPropertyChanged("PercentSuccessTrades");
                NotifyPropertyChanged("AverageWinningRRRTrades");
                NotifyPropertyChanged("AverageLosingRRRTrades");

                RunStrategyEnabled = false;
            }));

            var completed = 0;

            _producerConsumer = new ProducerConsumer<(IStrategy Strategy, MarketDetails Market)>(3, d =>
            {
                var strategyTester = new SimulationRunner(_candlesService, _tradeCalculatorService, _marketDetailsService, 
                    SimulationRunnerFlags.DoNotValidateStopsLimitsOrders | SimulationRunnerFlags.DoNotCacheM1Candles);
                var earliest = !string.IsNullOrEmpty(StartDate) ? (DateTime?)DateTime.Parse(StartDate) : null;
                var latest = !string.IsNullOrEmpty(EndDate) ? (DateTime?)DateTime.Parse(EndDate) : null;
                var result = strategyTester.Run(d.Strategy, d.Market, broker, earliest, latest, updatePrices: UpdatePrices);

                if (result != null)
                {
                    _results.AddResult(result);

                    // Adding trades to UI in realtime slows down the UI too much with strategies with many trades

                    completed++;
                    Log.Info($"Completed {completed}/{markets.Count * strategies.Count}");
                }

                return ProducerConsumerActionResult.Success;
            });

            foreach (var market in markets)
            {
                foreach (var strategy in strategies.Cast<IStrategy>())
                {
                    _producerConsumer.Add((strategy, _marketDetailsService.GetMarketDetails(broker.Name, market)));
                }
            }

            _producerConsumer.Start();
            _producerConsumer.SetProducerCompleted();
            _producerConsumer.WaitUntilConsumersFinished();

            stopwatch.Stop();
            Log.Info($"Simulation run completed in {stopwatch.Elapsed.TotalSeconds}s");

            // Save results
            if (File.Exists(_savedResultsPath))
            {
                File.Delete(_savedResultsPath);
            }
            File.WriteAllText(_savedResultsPath, JsonConvert.SerializeObject(_results.Results));

            _dispatcher.Invoke((Action)(() =>
            {
                _results.RaiseTestRunCompleted();

                NotifyPropertyChanged("TotalTrades");
                NotifyPropertyChanged("AverageRTrade");
                NotifyPropertyChanged("TotalR");
                NotifyPropertyChanged("PercentSuccessTrades");
                NotifyPropertyChanged("AverageWinningRRRTrades");
                NotifyPropertyChanged("AverageLosingRRRTrades");

                RunStrategyEnabled = true;
            }));
        }

        public void ViewClosing()
        {
            _producerConsumer?.Stop();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}