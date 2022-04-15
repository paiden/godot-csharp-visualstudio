using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Xml;
using EnvDTE;
using GodotAddinVS.Commands;
using GodotAddinVS.Debugging;
using GodotAddinVS.GodotMessaging;
using GodotTools.IdeMessaging;
using GodotTools.IdeMessaging.Requests;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace GodotAddinVS
{
    internal class GodotSolutionEventsListener : SolutionEventsListener
    {
        private static readonly object RegisterLock = new object();
        private bool _registered;

        private string _godotProjectDir;

        public string GodotProjectDir => _godotProjectDir;

        private DebuggerEvents DebuggerEvents { get; set; }

        private IServiceContainer ServiceContainer => (IServiceContainer)ServiceProvider;

        public string SolutionDir
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Solution.GetSolutionInfo(out string solutionDir, out string solutionFile, out string userOptsFile);
                _ = solutionFile;
                _ = userOptsFile;
                return solutionDir;
            }
        }

        public Client GodotMessagingClient { get; private set; }

        public GodotSolutionEventsListener(IServiceProvider serviceProvider)
            : base(serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Init();
        }

        public override int OnBeforeCloseProject(IVsHierarchy hierarchy, int removed)
        {
            return 0;
        }

        private static IEnumerable<Guid> ParseProjectTypeGuids(string projectTypeGuids)
        {
            string[] strArray = projectTypeGuids.Split(';');
            var guidList = new List<Guid>(strArray.Length);

            foreach (string input in strArray)
            {
                if (Guid.TryParse(input, out var result))
                    guidList.Add(result);
            }

            return guidList.ToArray();
        }

        private static bool IsGodotProject(IVsHierarchy hierarchy, Project dteProject)
        {
            return IsSdkStyleGodotProject(dteProject) || IsNonSkdStyleGodotProject(hierarchy);
        }

        private static bool IsNonSkdStyleGodotProject(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return hierarchy is IVsAggregatableProject aggregatableProject &&
                   aggregatableProject.GetAggregateProjectTypeGuids(out string projectTypeGuids) == 0 &&
                   ParseProjectTypeGuids(projectTypeGuids)
                       .Any(g => g == typeof(GodotFlavoredProjectFactory).GUID);
        }

        private static bool IsSdkStyleGodotProject(Project dteProject)
        {
            try
            {
                if (dteProject == null)
                {
                    return false;
                }

                ThreadHelper.ThrowIfNotOnUIThread();

                var doc = new XmlDocument();
                doc.Load(dteProject.FullName);

                var sdkAttribute = doc.DocumentElement?.Attributes["Sdk"];

                return sdkAttribute != null && sdkAttribute.Value.ToLowerInvariant().StartsWith("godot.net.sdk");
            }
            catch
            {
                return false;
            }
        }

        public override int OnAfterOpenProject(IVsHierarchy hierarchy, int added)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dteProject = TryGetDTEProject(hierarchy);

            if (!IsGodotProject(hierarchy, dteProject))
                return 0;

            if (IsSdkStyleGodotProject(dteProject))
            {
                SdkStyleLaunchMonoDebuggerCommand.SdkStyleProject = dteProject;
            }

            lock (RegisterLock)
            {
                if (_registered)
                    return 0;

                _godotProjectDir = EvalGodotProjectDir(dteProject);

                DebuggerEvents = ServiceProvider.GetService<DTE>().Events.DebuggerEvents;
                DebuggerEvents.OnEnterDesignMode += DebuggerEvents_OnEnterDesignMode;

                GodotMessagingClient?.Dispose();
                GodotMessagingClient = new Client(identity: "VisualStudio",
                    _godotProjectDir, new MessageHandler(), GodotPackage.Instance.Logger);
                GodotMessagingClient.Connected += OnClientConnected;
                GodotMessagingClient.Start();

                ServiceContainer.AddService(typeof(Client), GodotMessagingClient);

                _registered = true;
            }

            return 0;
        }

        public override int OnBeforeCloseSolution(object pUnkReserved)
        {
            lock (RegisterLock)
                _registered = false;
            Close();
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
            Close();
        }

        private void OnClientConnected()
        {
            var options = (GeneralOptionsPage)GodotPackage.Instance.GetDialogPage(typeof(GeneralOptionsPage));

            // If the setting is not yet assigned any value, set it to the currently connected Godot editor path
            if (string.IsNullOrEmpty(options.GodotExecutablePath))
            {
                string godotPath = GodotMessagingClient?.GodotEditorExecutablePath;
                if (!string.IsNullOrEmpty(godotPath) && File.Exists(godotPath))
                    options.GodotExecutablePath = godotPath;
            }
        }

        private void DebuggerEvents_OnEnterDesignMode(dbgEventReason reason)
        {
            if (reason != dbgEventReason.dbgEventReasonStopDebugging)
                return;

            if (GodotMessagingClient == null || !GodotMessagingClient.IsConnected)
                return;

            var currentDebugTarget = GodotDebugTargetSelection.Instance.CurrentDebugTarget;

            if (currentDebugTarget != null && currentDebugTarget.ExecutionType == ExecutionType.PlayInEditor)
                _ = GodotMessagingClient.SendRequest<StopPlayResponse>(new StopPlayRequest());
        }

        private void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (GodotMessagingClient != null)
            {
                ServiceContainer.RemoveService(typeof(Client));
                GodotMessagingClient.Dispose();
                GodotMessagingClient = null;
            }

            if (DebuggerEvents != null)
            {
                DebuggerEvents.OnEnterDesignMode -= DebuggerEvents_OnEnterDesignMode;
                DebuggerEvents = null;
            }
        }

        private string EvalGodotProjectDir(Project dteProject)
        {
            try
            {
                if (dteProject == null)
                {
                    return SolutionDir;
                }

                ThreadHelper.ThrowIfNotOnUIThread();

                var evalProj = new Microsoft.Build.Evaluation.Project(dteProject.FullName);
                var evaluated = evalProj.GetPropertyValue("GodotProjectDir");
                return string.IsNullOrWhiteSpace(evaluated) ? SolutionDir : evaluated;
            }
            catch
            {
                return SolutionDir;
            }
        }

        private static EnvDTE.Project TryGetDTEProject(IVsHierarchy hierarchy)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var itemid = VSConstants.VSITEMID_ROOT;

                object objProj;
                hierarchy.GetProperty(itemid, (int)__VSHPROPID.VSHPROPID_ExtObject, out objProj);

                return objProj as EnvDTE.Project;
            }
            catch
            {
                return null;
            }
        }
    }
}
