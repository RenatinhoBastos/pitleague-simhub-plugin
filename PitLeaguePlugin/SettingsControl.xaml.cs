using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PitLeague.SimHub
{
    public partial class SettingsControl : UserControl
    {
        private readonly PitLeaguePlugin _plugin;
        private bool _loading = true;

        public SettingsControl(PitLeaguePlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _plugin.StatusChanged += Plugin_StatusChanged;
            LoadSettings();
            _loading = false;
        }

        private void LoadSettings()
        {
            var s = _plugin.Settings;

            TxtApiKey.Password = s.ApiKey;
            TxtLeagueId.Text = s.LeagueId;
            TxtMinDrivers.Text = s.MinDriversToSend.ToString();
            ChkAutoSend.IsChecked = s.AutoSendOnRaceEnd;
            ChkRaceOnly.IsChecked = s.RaceOnlyMode;
            ChkDebug.IsChecked = s.DebugMode;
            TxtVersion.Text = " v" + PitLeaguePlugin.VERSION;

            foreach (ComboBoxItem item in CmbGame.Items)
            {
                if (item.Content.ToString().StartsWith(s.GameDisplayName))
                {
                    CmbGame.SelectedItem = item;
                    break;
                }
            }

            if (s.LastSentAt > DateTime.MinValue)
                TxtLastSent.Text = $"{s.LastSendStatus}\n{s.LastSentAt:dd/MM/yyyy HH:mm} UTC";
            else
                TxtLastSent.Text = "Nunca enviado";

            UpdateStatusUI();
        }

        private void UpdateStatusUI()
        {
            TxtStatus.Text = _plugin.LastStatusMessage;

            if (_plugin.IsConnected)
            {
                EllipseConnected.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                TxtConnected.Text = "Conectado";
            }
            else
            {
                EllipseConnected.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                TxtConnected.Text = "Não verificado";
            }

            TxtReadyHint.Text = _plugin.ResultReadyToSend
                ? "Resultado pronto para enviar."
                : "Nenhuma corrida finalizada detectada nesta sessão.";
        }

        private void Plugin_StatusChanged(object sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(UpdateStatusUI);
        }

        // ─── Event handlers ───────────────────────────────────────────────────

        private void TxtApiKey_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.ApiKey = TxtApiKey.Password;
        }

        private void TxtLeagueId_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.LeagueId = TxtLeagueId.Text.Trim();
        }

        private void CmbGame_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var item = CmbGame.SelectedItem as ComboBoxItem;
            if (item != null)
            {
                var full = item.Content.ToString();
                var name = full.Contains(" (") ? full.Substring(0, full.IndexOf(" (")) : full;
                _plugin.Settings.GameDisplayName = name == "Outro" ? "" : name;
            }
        }

        private void ChkAutoSend_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.AutoSendOnRaceEnd = ChkAutoSend.IsChecked == true;
        }

        private void ChkRaceOnly_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.RaceOnlyMode = ChkRaceOnly.IsChecked == true;
        }

        private void ChkDebug_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.DebugMode = ChkDebug.IsChecked == true;
        }

        private void TxtMinDrivers_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(TxtMinDrivers.Text, out int val) && val > 0)
                _plugin.Settings.MinDriversToSend = val;
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            global::SimHub.Logging.Current.Info("[PitLeague] Botao 'Testar Conexao' clicado pelo usuario");
            BtnTest.IsEnabled = false;
            BtnTest.Content = "...";
            await _plugin.TestConnection();
            BtnTest.Content = "Testar";
            BtnTest.IsEnabled = true;
        }

        private void BtnCaptureNow_Click(object sender, RoutedEventArgs e)
        {
            global::SimHub.Logging.Current.Info("[PitLeague] Botao 'Capturar Resultado Agora' clicado pelo usuario");

            try
            {
                _plugin.ForceCaptureCurrentState();
            }
            catch (Exception ex)
            {
                global::SimHub.Logging.Current.Error($"[PitLeague] Exception em CaptureNow: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Erro: {ex.GetType().Name}: {ex.Message}", "PitLeague", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnSendResult_Click(object sender, RoutedEventArgs e)
        {
            global::SimHub.Logging.Current.Info("[PitLeague] Botao 'Enviar Resultado' clicado pelo usuario");
            if (!_plugin.ResultReadyToSend)
            {
                MessageBox.Show(
                    "Nenhuma corrida finalizada detectada.\nConclua uma corrida antes de enviar.",
                    "PitLeague", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSendResult.IsEnabled = false;
            BtnSendResult.Content = "Enviando...";

            await _plugin.SendResultFromSnapshot();

            BtnSendResult.Content = "Enviar Resultado Agora";
            BtnSendResult.IsEnabled = true;

            var s = _plugin.Settings;
            if (s.LastSentAt > DateTime.MinValue)
                TxtLastSent.Text = $"{s.LastSendStatus}\n{s.LastSentAt:dd/MM/yyyy HH:mm} UTC";
        }
    }
}
