using System;
using System.Linq;
using System.Windows.Threading;
using StatisticsAnalysisTool.DamageMeter;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Overlay
{
    /// <summary>
    /// Integration layer that connects the application's ViewModels to the overlay system
    /// without polluting the existing codebase with overlay-specific logic.
    /// </summary>
    public class OverlayIntegration : IDisposable
    {
        private readonly MainWindowViewModel _mainWindowViewModel;
        private bool _isSubscribed;
        private DispatcherTimer _damageTimer;

        public OverlayIntegration(MainWindowViewModel mainWindowViewModel)
        {
            _mainWindowViewModel = mainWindowViewModel ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
        }

        /// <summary>
        /// Start monitoring ViewModel changes and pushing updates to overlay system.
        /// </summary>
        public void Start()
        {
            if (_isSubscribed) return;

            // Subscribe to DamageMeter collection changes and fragment property changes
            if (_mainWindowViewModel.DamageMeterBindings?.DamageMeter != null)
            {
                var damageMeter = _mainWindowViewModel.DamageMeterBindings.DamageMeter;
                damageMeter.CollectionChanged += DamageMeter_CollectionChanged;
                foreach (var frag in damageMeter)
                {
                    frag.PropertyChanged += DamageMeterFragment_PropertyChanged;
                }
                Serilog.Log.Debug("[OverlayIntegration] Subscribed to DamageMeter updates and fragment property changes");
            }

            // Subscribe to dashboard property changes for event-driven overlay updates
            if (_mainWindowViewModel.DashboardBindings != null)
            {
                _mainWindowViewModel.DashboardBindings.PropertyChanged += DashboardBindings_PropertyChanged;
                Serilog.Log.Debug("[OverlayIntegration] Subscribed to DashboardBindings property changes for overlay updates");
            }

            // Start damage polling timer (every 2 seconds)
            _damageTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _damageTimer.Tick += DamageTimer_Tick;
            _damageTimer.Start();
            Serilog.Log.Debug("[OverlayIntegration] Damage polling timer started");

            _isSubscribed = true;
        }

        /// <summary>
        /// Stop monitoring and unsubscribe from all events.
        /// </summary>
        public void Stop()
        {
            if (!_isSubscribed) return;

            try
            {
                if (_mainWindowViewModel.DamageMeterBindings?.DamageMeter != null)
                {
                    var damageMeter = _mainWindowViewModel.DamageMeterBindings.DamageMeter;
                    damageMeter.CollectionChanged -= DamageMeter_CollectionChanged;
                    foreach (var frag in damageMeter)
                    {
                        frag.PropertyChanged -= DamageMeterFragment_PropertyChanged;
                    }
                }

                if (_mainWindowViewModel.DashboardBindings != null)
                {
                    _mainWindowViewModel.DashboardBindings.PropertyChanged -= DashboardBindings_PropertyChanged;
                    Serilog.Log.Debug("[OverlayIntegration] Unsubscribed from DashboardBindings property changes");
                }

                if (_damageTimer != null)
                {
                    _damageTimer.Stop();
                    _damageTimer.Tick -= DamageTimer_Tick;
                    _damageTimer = null;
                    Serilog.Log.Debug("[OverlayIntegration] Damage polling timer stopped");
                }

                _isSubscribed = false;
                Serilog.Log.Debug("[OverlayIntegration] Unsubscribed from updates");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Error while unsubscribing from updates");
            }
        }

        public void Dispose()
        {
            Stop();
        }


        private void DashboardBindings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Push dashboard overlay update on any property change
            PushDashboardToOverlay();
            try
            {
                var mgr = OverlaySectionManager.Instance;
                mgr?.UpdateRepair(_mainWindowViewModel.DashboardBindings);
            }
            catch { }
        }

        private void DamageMeter_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Subscribe/unsubscribe to PropertyChanged for live updates
            if (e.NewItems != null)
            {
                foreach (var frag in e.NewItems)
                {
                    if (frag is DamageMeterFragment f)
                        f.PropertyChanged += DamageMeterFragment_PropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (var frag in e.OldItems)
                {
                    if (frag is DamageMeterFragment f)
                        f.PropertyChanged -= DamageMeterFragment_PropertyChanged;
                }
            }
            Serilog.Log.Debug($"[OverlayIntegration] DamageMeter collection changed: Action={e.Action}, NewItems={e.NewItems?.Count ?? 0}, OldItems={e.OldItems?.Count ?? 0}");
            PushDamageMeterToOverlay();
        }


        private void DamageMeterFragment_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Push overlay update on any property change
            Serilog.Log.Debug($"[OverlayIntegration] DamageMeterFragment property changed: {e.PropertyName}");
            PushDamageMeterToOverlay();
        }

        private void PushDamageMeterToOverlay()
        {
            try
            {
                var mgr = OverlaySectionManager.Instance;
                // Only push if overlay is enabled and at least one client is connected (mirror dashboard logic)
                var overlayOptions = StreamingOverlayViewModel.Instance?.OverlayOptions;
                var serverField = overlayOptions?.GetType().GetField("_overlayServer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                var server = serverField?.GetValue(overlayOptions) as StatisticsAnalysisTool.Network.Overlay.OverlayServer;
                bool overlayEnabled = overlayOptions?.IsOverlayEnabled ?? false;
                bool hasClients = server?._wsModule?.GetActiveContexts()?.Any(ctx => ctx.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open) ?? false;
                if (overlayEnabled && hasClients)
                {
                    Serilog.Log.Debug("[OverlayIntegration] Pushing damage meter data to overlay...");
                    mgr?.UpdateDamage(_mainWindowViewModel.DamageMeterBindings);
                    Serilog.Log.Debug("[OverlayIntegration] Damage meter data push completed");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to push damage meter to overlay");
            }
        }


        private void DamageTimer_Tick(object sender, EventArgs e)
        {
            // Push damage updates to overlay periodically
            PushDamageMeterToOverlay();
        }

        private void PushDashboardToOverlay()
        {
            try
            {
                var mgr = OverlaySectionManager.Instance;
                // Only push if overlay is enabled
                var overlayOptions = StreamingOverlayViewModel.Instance?.OverlayOptions;
                bool overlayEnabled = overlayOptions?.IsOverlayEnabled ?? false;
                if (overlayEnabled)
                {
                    // Force push dashboard to overlay regardless of current client count so that
                    // connected clients receive immediate updates and new clients receive fresh state.
                    mgr?.UpdateDashboard(_mainWindowViewModel.DashboardBindings, true);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to push dashboard to overlay");
            }
            try
            {
                var mgr = OverlaySectionManager.Instance;
                var overlayOptions2 = StreamingOverlayViewModel.Instance?.OverlayOptions;
                bool overlayEnabled2 = overlayOptions2?.IsOverlayEnabled ?? false;
                if (overlayEnabled2)
                {
                    // Only force a damage push if the overlay server has connected clients for the damage section.
                    try
                    {
                        var serverField = overlayOptions2?.GetType().GetField("_overlayServer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var server = serverField?.GetValue(overlayOptions2) as StatisticsAnalysisTool.Network.Overlay.OverlayServer;
                        bool hasDamageClients = server?._wsModule?.GetActiveContexts()?.Any(ctx =>
                        {
                            try
                            {
                                string clientSection = null;
                                server._wsModule._clientSections.TryGetValue(ctx.Id, out clientSection);
                                return ctx.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open && clientSection == "damage";
                            }
                            catch { return false; }
                        }) ?? false;

                        if (hasDamageClients)
                        {
                            Serilog.Log.Debug("[OverlayIntegration] Forcing damage meter data push to overlay (clients present)...");
                            mgr?.UpdateDamage(_mainWindowViewModel.DamageMeterBindings, true);
                            Serilog.Log.Debug("[OverlayIntegration] Damage meter data push completed");
                        }
                        else
                        {
                            // No clients listening for damage — avoid forcing a broadcast and producing log spam.
                            //Serilog.Log.Debug("[OverlayIntegration] Skipping forced damage push: no connected damage clients");
                        }
                    }
                    catch (Exception exInner)
                    {
                        Serilog.Log.Warning(exInner, "[OverlayIntegration] Error while checking overlay server clients for damage push");
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to push damage meter to overlay");
            }
        }

        /// <summary>
        /// Force immediate push of both dashboard and damage overlays.
        /// Call this after overlay is enabled or started to ensure overlays are up-to-date.
        /// </summary>
        public void ForceInitialOverlayPush()
        {
            // Simpler initial push: ensure dashboard, damage, and repair payloads are pushed once.
            try
            {
                PushDashboardToOverlay();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to force dashboard overlay push");
            }

            try
            {
                PushDamageMeterToOverlay();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to force damage overlay push");
            }

            try
            {
                var mgr = OverlaySectionManager.Instance;
                mgr?.UpdateRepair(_mainWindowViewModel.DashboardBindings, true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OverlayIntegration] Failed to force repair overlay push");
            }
        }
    }
}
