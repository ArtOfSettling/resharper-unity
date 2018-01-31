﻿using System;
using System.Diagnostics;
using System.IO;
using JetBrains.Application.Threading;
using JetBrains.DataFlow;
using JetBrains.DocumentModel;
using JetBrains.IDE;
using JetBrains.Platform.RdFramework;
using JetBrains.Platform.RdFramework.Base;
using JetBrains.Platform.RdFramework.Impl;
using JetBrains.Platform.RdFramework.Tasks;
using JetBrains.Platform.RdFramework.Util;
using JetBrains.Platform.Unity.Model;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Host.Features;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Rider.Model;
using JetBrains.TextControl;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Newtonsoft.Json;

namespace JetBrains.ReSharper.Plugins.Unity.Rider
{
    [SolutionComponent]
    public class UnityPluginProtocolController
    {
        private readonly Lifetime myLifetime;
        private readonly SequentialLifetimes mySessionLifetimes;
        private readonly ILogger myLogger;
        private readonly IScheduler myDispatcher;
        private readonly IShellLocks myLocks;
        private readonly ISolution mySolution;
        private readonly RdUnityModel myHost;
        public UnityModel UnityModel;
        private Protocol myProtocol;

        public readonly ISignal<UnityModel> Refresh = new DataFlow.Signal<UnityModel>("Refresh");

        public UnityPluginProtocolController(Lifetime lifetime, ILogger logger, 
            IScheduler dispatcher, IShellLocks locks, ISolution solution, RdUnityModel host)
        {
            if (!ProjectExtensions.IsSolutionGeneratedByUnity(solution.SolutionFilePath.Directory))
                return;

            myLifetime = lifetime;
            myLogger = logger;
            myDispatcher = dispatcher;
            myLocks = locks;
            mySolution = solution;
            myHost = host;
            mySessionLifetimes = new SequentialLifetimes(lifetime);

            var solFolder = mySolution.SolutionFilePath.Directory;
            AdviseCustomDataFromFrontend();

            var protocolInstancePath = solFolder.Combine(
                "Library/ProtocolInstance.json"); // todo: consider non-Unity Solution with Unity-generated projects

            if (!protocolInstancePath.ExistsFile)
                File.Create(protocolInstancePath.FullPath);

            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.Path = protocolInstancePath.Directory.FullPath;
            watcher.NotifyFilter =
                NotifyFilters.LastAccess |
                NotifyFilters.LastWrite; //Watch for changes in LastAccess and LastWrite times
            watcher.Filter = protocolInstancePath.Name;

            // Add event handlers.
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;

            watcher.EnableRaisingEvents = true; // Begin watching.

            // connect on start of Rider
            CreateProtocol(protocolInstancePath);
        }        
        
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            var protocolInstancePath = FileSystemPath.Parse(e.FullPath);
            // connect on reload of server
            myLocks.ExecuteOrQueue(myLifetime, "CreateProtocol", ()=> CreateProtocol(protocolInstancePath));
        }

        private void AdviseCustomDataFromFrontend()
        {
            myHost.Data.Advise(myLifetime, e =>
            {
                if (e.NewValue == e.OldValue || e.NewValue == null) return;
                switch (e.Key)
                {
                    case "UNITY_Refresh":
                        if (e.NewValue.ToLower() == true.ToString().ToLower())
                        {
                            myLogger.Info($"{e.Key} = {e.NewValue} came from frontend.");
                            if (UnityModel != null) 
                              Refresh.Fire(UnityModel);    
                        }
                        break;
                    case "UNITY_Step":
                        if (e.NewValue.ToLower() == true.ToString().ToLower())
                        {
                            myLogger.Info($"{e.Key} = {e.NewValue} came from frontend.");
                            UnityModel?.Step.Sync(RdVoid.Instance, RpcTimeouts.Default);
                        }
                        break;
                    case "UNITY_Play":
                        myLogger.Info($"{e.Key} = {e.NewValue} came from frontend.");
                        UnityModel?.Play.SetValue(Convert.ToBoolean(e.NewValue));
                        break;
                        
                    case "UNITY_Pause":
                        myLogger.Info($"{e.Key} = {e.NewValue} came from frontend.");
                        UnityModel?.Pause.SetValue(Convert.ToBoolean(e.NewValue));
                        break;
                }
            });
        }

        private void CreateProtocol(FileSystemPath protocolInstancePath)
        {
            int port;
            try
            {
                var protocolInstance = JsonConvert.DeserializeObject<ProtocolInstance>(protocolInstancePath.ReadAllText2().Text);
                port = protocolInstance.port_id;
            }
            catch (Exception e)
            {
                myLogger.Warn($"Unable to parse {protocolInstancePath}" + Environment.NewLine + e);
                return;
            }
            
            myLogger.Info($"UNITY_Port {port}.");

            try
            {
                myLogger.Info("Create protocol...");
                var lifetime = mySessionLifetimes.Next();
                myLogger.Info("Creating SocketWire with port = {0}", port);
                var wire = new SocketWire.Client(lifetime, myDispatcher, port, "UnityClient");
                myProtocol = new Protocol(new Serializers(), new Identities(IdKind.Client), myDispatcher, wire);
                UnityModel = new UnityModel(lifetime, myProtocol);
                UnityModel.IsClientConnected.Set(rdVoid => true);
                UnityModel.RiderProcessId.SetValue(Process.GetCurrentProcess().Id);
                SetOrCreateDataKeyValuePair("UNITY_SessionInitialized", "true");
                
                SubscribeToLogs(lifetime);
                SubscribeToOpenFile();
                UnityModel?.Play.AdviseNotNull(myLifetime, b => SetOrCreateDataKeyValuePair("UNITY_Play", b.ToString().ToLower()));
                UnityModel?.Pause.AdviseNotNull(myLifetime, b => SetOrCreateDataKeyValuePair("UNITY_Pause", b.ToString().ToLower()));
            }
            catch (Exception ex)
            {
                myLogger.Error(ex);
            }
        }

        private void SubscribeToOpenFile()
        {
            UnityModel.OpenFileLineCol.Set(args =>
            {
                using (ReadLockCookie.Create())
                {
                    var textControl = mySolution.GetComponent<IEditorManager>()
                        .OpenFile(FileSystemPath.Parse(args.Path), OpenFileOptions.DefaultActivate);
                    if (textControl == null)
                        return false;
                    if (args.Line > 0 || args.Col > 0)
                    {
                        textControl.Caret.MoveTo((Int32<DocLine>) (args.Line - 1), (Int32<DocColumn>) args.Col,
                            CaretVisualPlacement.Generic);
                    }
                }

                SetOrCreateDataKeyValuePair("UNITY_ActivateRider", "true");
                return true;
            });
        }

        private void SetOrCreateDataKeyValuePair(string key, string value)
        {
            var data = myHost.Data;
            if (data.ContainsKey(key))
                data[key] = value;
            else
                data.Add(key, value);
        }

        private void SubscribeToLogs(Lifetime lifetime)
        {
            UnityModel.LogModelInitialized.Advise(lifetime, modelInitialized =>
            {
                modelInitialized.Log.Advise(lifetime, entry =>
                {
                    myLogger.Verbose(entry.Mode +" " + entry.Type +" "+ entry.Message +" "+ Environment.NewLine +" "+ entry.StackTrace);
                    SetOrCreateDataKeyValuePair("UNITY_LogEntry", JsonConvert.SerializeObject(entry));
                });
            });
        }
    }

    // ReSharper disable once ClassNeverInstantiated.Global
    class ProtocolInstance
    {
        // ReSharper disable once InconsistentNaming
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public int port_id { get; set; }
    }
}