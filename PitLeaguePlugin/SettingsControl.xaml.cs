using System;
using System.Threading.Tasks;
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

            // Escutar mudanças de status do plugin
            _plugin.StatusChanged += Plugin_StatusChanged;

            LoadSettings();
            _loading = false;
        }

        private void LoadSettings()
        {
            var s = _plugin.Settings;

            TxtApiKey.Password  = s.ApiKey;
            TxtLeagueId.Text    = s.LeagueId;
            TxtMinDrivers.Text  = s.MinDriversToSend.ToString();
            ChkAutoSend.IsChecked = s.AutoSendOnRaceEnd;
            ChkRaceOnly.IsChecked = s.RaceOnlyMode;
            ChkDebug.IsChecked    = s.DebugMode;
            TxtVersion.Text       = " v" + PitLeaguePlugin.VERSION;

            // Selecionar jogo no combobox
            foreach (ComboBoxItem item in CmbGame.Items)
            {
                if (item.Content.ToString().StartsWith(s.GameDisplayName))
                {
                    CmbGame.SelectedItem = item;
                    break;
                }
            }

            // Último envio
            if (s.LastSentAt > DateTime.MinValue)
                TxtLastSent.Text = $"{s.LastSendStatus}\n{s.LastSentAt:dd/MM/yyyy HH:mm} UTC";
            else
                TxtLastSent.Text = "Nunca enviado";

            UpdateStatusUI();
        }

        private void UpdateStatusUI()
        {
            TxtStatus.Text = _plugin.LastStatusMessage;

            // Indicador de conexão
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
        }

        private void Plugin_StatusChanged(object sender, EventArgs e)
        {
            // Atualizar UI na thread do UI
            Dispatcher.InvokeAsync(UpdateStatusUI);
        }

        // ─── Event handlers ───────────────────────────────────────────────────

        private void TxtApiKey_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.ApiKey = TxtApiKey.Password;
            SaveSettings();
        }

        private void TxtLeagueId_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.LeagueId = TxtLeagueId.Text.Trim();
            SaveSettings();
        }

        private void CmbGame_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var item = CmbGame.SelectedItem as ComboBoxItem;
            if (item != null)
            {
                // Pegar só o nome antes do " (" se houver
                var full = item.Content.ToString();
                var name = full.Contains(" (") ? full.Substring(0, full.IndexOf(" (")) : full;
                _plugin.Settings.GameDisplayName = name == "Outro" ? "" : name;
                SaveSettings();
            }
        }

        private void ChkAutoSend_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.AutoSendOnRaceEnd = ChkAutoSend.IsChecked == true;
            SaveSettings();
        }

        private void ChkRaceOnly_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.RaceOnlyMode = ChkRaceOnly.IsChecked == true;
            SaveSettings();
        }

        private void ChkDebug_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _plugin.Settings.DebugMode = ChkDebug.IsChecked == true;
            SaveSettings();
        }

        private void TxtMinDrivers_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(TxtMinDrivers.Text, out int val) && val > 0)
            {
                _plugin.Settings.MinDriversToSend = val;
                SaveSettings();
            }
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            BtnTest.IsEnabled = false;
            BtnTest.Content = "...";
            await _plugin.TestConnection();
            BtnTest.Content = "Testar";
            BtnTest.IsEnabled = true;
        }

        private async void BtnSendResult_Click(object sender, RoutedEventArgs e)
        {
            var data = _plugin.PluginManager?.GameData;
            if (data == null)
            {
                MessageBox.Show(
                    "Nenhum jogo ativo detectado pelo SimHub.\nAbra um jogo e conclua uma corrida antes de enviar.",
                    "PitLeague",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            BtnSendResult.IsEnabled = false;
            BtnSendResult.Content = "Enviando...";

            await _plugin.SendResult(_plugin.PluginManager, data.Value);

            BtnSendResult.Content = "🚀  Enviar Resultado Agora";
            BtnSendResult.IsEnabled = true;

            // Atualizar último envio
            var s = _plugin.Settings;
            if (s.LastSentAt > DateTime.MinValue)
                TxtLastSent.Text = $"{s.LastSendStatus}\n{s.LastSentAt:dd/MM/yyyy HH:mm} UTC";
        }

        private void SaveSettings()
        {
            _plugin.PluginManager?.SetPropertyValue(
                "PitLeague.Connected", typeof(PitLeaguePlugin), _plugin.IsConnected);
        }
    }
}
