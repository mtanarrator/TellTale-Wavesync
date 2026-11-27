using System;
using System.Collections;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using ScriptPortal.Vegas;
using VegasAudioSync.UI;

namespace VegasAudioSync
{
    /// <summary>
    /// Extension entry point discovered and loaded by VEGAS Pro.
    /// Registers the "TellTale WaveSync" dockable panel under View -> Extensions.
    /// </summary>
    public class TellTaleWaveSyncCommandModule : ICustomCommandModule
    {
        public const string CommandDisplayName = "TellTale WaveSync";
        public const string DockIdent = "TellTaleWaveSyncDockView";

        private Vegas _vegas;
        private readonly CustomCommand _syncCommand = new CustomCommand(CommandCategory.View, "TellTaleWaveSyncCmd");

        public void InitializeModule(Vegas vegas)
        {
            _vegas = vegas;
            _syncCommand.DisplayName = CommandDisplayName;
        }

        public ICollection GetCustomCommands()
        {
            _syncCommand.MenuPopup += OnMenuPopup;
            _syncCommand.Invoked += OnCommandInvoked;
            return new CustomCommand[] { _syncCommand };
        }

        private void OnMenuPopup(object sender, EventArgs e)
        {
            _syncCommand.Checked = _vegas.FindDockView(DockIdent);
        }

        private void OnCommandInvoked(object sender, EventArgs e)
        {
            if (!_vegas.ActivateDockView(DockIdent))
            {
                var dockView = new TellTaleWaveSyncDockableControl(_vegas)
                {
                    AutoLoadCommand = _syncCommand,
                    PersistDockWindowState = true
                };
                _vegas.LoadDockView(dockView);
            }
        }
    }

    /// <summary>
    /// WinForms host dockable control hosting the modernized WPF VegasAudioSyncControl.
    /// </summary>
    public class TellTaleWaveSyncDockableControl : DockableControl
    {
        private readonly Vegas _vegas;
        private ElementHost _elementHost;
        private VegasAudioSyncControl _wpfControl;

        public TellTaleWaveSyncDockableControl(Vegas vegas) : base(TellTaleWaveSyncCommandModule.DockIdent)
        {
            _vegas = vegas;
            DisplayName = TellTaleWaveSyncCommandModule.CommandDisplayName;
            BackColor = Color.FromArgb(14, 16, 21); // Dark Obsidian #0E1015
        }

        public override DockWindowStyle DefaultDockWindowStyle => DockWindowStyle.Docked;

        public override Size DefaultFloatingSize => new Size(460, 860);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            try
            {
                // Keep the hosted WPF panel usable when the dock/floating window is small.
                MinimumSize = new Size(420, 620);
            }
            catch
            {
                // Some host wrappers do not expose MinimumSize; sizing is best-effort.
            }

            try
            {
                _wpfControl = new VegasAudioSyncControl(_vegas);
                _elementHost = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    Child = _wpfControl,
                    BackColor = Color.FromArgb(14, 16, 21)
                };
                Controls.Add(_elementHost);
            }
            catch (Exception ex)
            {
                Controls.Clear();
                Controls.Add(new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = Color.FromArgb(14, 16, 21),
                    ForeColor = Color.FromArgb(255, 123, 114),
                    Text = "TellTale WaveSync failed to load UI.\r\n\r\n" + ex
                });
            }
        }
    }
}
