// Copyright 2007-2008 The Apache Software Foundation.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace Topshelf.Model
{
    using System;
    using System.Diagnostics;
    using Exceptions;
    using log4net;
    using Magnum.Channels;
    using Magnum.StateMachine;
    using Messages;
    using Shelving;

    [DebuggerDisplay("Service({Name}) is {State}")]
    public class ServiceController<TService> :
        StateMachine<ServiceController<TService>>,
        IServiceController
        where TService : class
    {
        ILog _log = LogManager.GetLogger(typeof(ServiceController<TService>));

        #region StateMachine

        static ServiceController()
        {
            Define(() =>
            {
                Initially(
                    When(OnStart)
                        .Then(sc => sc.Initialize())
                        .Then(sc => sc.StartAction(sc._instance))
                        .TransitionTo(Started)
                    );

                During(Started,
                       When(OnPause)
                           .Then(sc => sc.PauseAction(sc._instance))
                           .TransitionTo(Paused));

                During(Paused,
                       When(OnContinue)
                           .Then(sc => sc.ContinueAction(sc._instance))
                           .TransitionTo(Started));


                Anytime(
                    When(OnStop)
                        .Then(sc => sc.StopAction(sc._instance))
                        .TransitionTo(Stopped)
                    );
            });
        }

        public static Event OnStart { get; set; }
        public static Event OnStop { get; set; }
        public static Event OnPause { get; set; }
        public static Event OnContinue { get; set; }

        public static State Initial { get; set; }
        public static State Started { get; set; }
        public static State Stopped { get; set; }
        public static State Paused { get; set; }
        public static State Completed { get; set; }

        #endregion

        #region Messaging Start

        readonly UntypedChannel _hostChannel;
        readonly WcfChannelHost _myChannelHost;
        readonly ChannelAdapter _myChannel;
        readonly ChannelConnection _connection;

        public UntypedChannel ControllerChannel
        {
            get
            {
                return _myChannel;
            }
        }

        #endregion

        public ServiceController()
        {
            _hostChannel = WellknownAddresses.GetHostChannelProxy();
            _myChannel = new ChannelAdapter();

            //TODO: this will error in multiple hosted services - stuff style
            _myChannelHost = WellknownAddresses.GetCurrentShelfHost(_myChannel); //service name?

            //build subscriptions
            _connection = _myChannel.Connect(s =>
            {
                s.Consume<ReadyService>().Using(m => Initialize());
                s.Consume<StopService>().Using(m => Stop());
                s.Consume<StartService>().Using(m => Start());
                s.Consume<PauseService>().Using(m => Pause());
                s.Consume<ContinueService>().Using(m => Continue());
            });

        }

        TService _instance;
        public Action<TService> StartAction { get; set; }
        public Action<TService> StopAction { get; set; }
        public Action<TService> PauseAction { get; set; }
        public Action<TService> ContinueAction { get; set; }
        public ServiceBuilder BuildService { get; set; }

        #region Dispose Stuff

        bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (!disposing) return;
            if (!_disposed) return;

            if(_connection != null)
                _connection.Dispose();
            
            if(_myChannelHost != null)
                _myChannelHost.Dispose();
            
            _instance = default(TService);
            StartAction = null;
            StopAction = null;
            PauseAction = null;
            ContinueAction = null;
            BuildService = null;
            _disposed = true;
        }

        ~ServiceController()
        {
            Dispose(false);
        }

        #endregion

        #region IServiceController Members

        public void Initialize()
        {
            _instance = (TService)BuildService(Name);
            
            //TODO: send fault
            if (_instance == null) 
                throw new CouldntBuildServiceException(Name, typeof(TService));


            _hostChannel.Send(new ServiceReady());

        }

        public void Start()
        {
            try
            {
                _hostChannel.Send(new ServiceStarting());
                RaiseEvent(OnStart);
                _hostChannel.Send(new ServiceStarted());
            }
            catch (Exception ex)
            {
                SendFault(ex);
            }
        }

        public void Stop()
        {
            try
            {
                _hostChannel.Send(new ServiceStopping());
                RaiseEvent(OnStop);
                _hostChannel.Send(new ServiceStopped());
            }
            catch (Exception ex)
            {
                SendFault(ex);
            }
            
        }

        public void Pause()
        {
            try
            {
                _hostChannel.Send(new ServicePausing());
                RaiseEvent(OnPause);
                _hostChannel.Send(new ServicePaused());
            }
            catch (Exception ex)
            {
                SendFault(ex);
            }

        }

        public void Continue()
        {
            try
            {
                _hostChannel.Send(new ServiceContinuing());
                RaiseEvent(OnContinue);
                _hostChannel.Send(new ServiceContinued());
            }
            catch (Exception ex)
            {
                SendFault(ex);
            }
        }

        //dispose 

        private void SendFault(Exception exception)
        {
            try
            {
                _hostChannel.Send(new ShelfFault(exception));
            }
            catch (Exception)
            {
                _log.Error("Shelf '{0}' is having a bad day.", exception);
                // eat the exception for now
            }
        }

        public string Name { get; set; }

        public Type ServiceType
        {
            get { return typeof(TService); }
        }

        public ServiceState State
        {
            get { return (ServiceState) Enum.Parse(typeof(ServiceState), CurrentState.Name, true); }
        }

        #endregion
    }
}