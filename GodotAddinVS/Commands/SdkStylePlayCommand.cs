using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Net;
using GodotAddinVS.Debugging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Mono.Debugging.Soft;
using Mono.Debugging.VisualStudio;
using Task = System.Threading.Tasks.Task;

namespace GodotAddinVS.Commands
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class SdkStylePlayCommand
    {
        public static EnvDTE.Project SdkStyleProject { set; private get; } = null;

        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0130;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("4d8e3995-f0d4-4a7d-a702-c92b59de5875");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="SdkStylePlayCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private SdkStylePlayCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static SdkStylePlayCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in SdkStylePlayCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new SdkStylePlayCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string message = string.Format(CultureInfo.CurrentCulture, "Inside {0}.MenuItemCallback()", this.GetType().FullName);
            string title = "SdkStylePlayCommand";

            // Show a message box to prove we were here
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            LaunchMonoDebugger();

        }

        private void LaunchMonoDebugger()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var random = new Random(DateTime.Now.Millisecond);
            var port = 8800 + random.Next(0, 100);

            var _baseProject = SdkStyleProject;
            var startArgs = new SoftDebuggerListenArgs(_baseProject.Name, IPAddress.Loopback, port) { MaxConnectionAttempts = 3 };

            var startInfo = new GodotStartInfo(startArgs, null, _baseProject) { WorkingDirectory = GodotPackage.Instance.GodotSolutionEventsListener?.SolutionDir };
            var session = new GodotDebuggerSession();

            var launcher = new MonoDebuggerLauncher(new Progress<string>());

            launcher.StartSession(startInfo, session);
        }
    }
}
