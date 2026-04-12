' ============================================================
' MainWindow.xaml.vb  ← ARCHIVO PRINCIPAL
' Contenido: declaración de campos, constructor, lifecycle
'   de la ventana y helpers compartidos por todos los tabs.
'
' Los demás archivos son Partial Class del mismo MainWindow:
'   MainWindow.Streaming.vb  → Tab Gráfico + Tab Streaming
'   MainWindow.Descarga.vb   → Tab Descarga Histórica
'   MainWindow.Trading.vb    → Tab Trading Live
'   MainWindow.ApiServer.vb  → Tab Server + Análisis
' ============================================================
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports System.Windows.Data
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Partial Public Class MainWindow
        Inherits System.Windows.Window

        ' ===== Constantes y config =====
        Private _apiUri As String = "wss://ws.derivws.com/websockets/v3?app_id=1089"
        Private Const PLATFORM As String = "Deriv"

        ' ===== Streaming (Tab Streaming) =====
        Private _webSocket As ClientWebSocket
        Private _cancellationTokenSource As CancellationTokenSource
        Private _candles As ObservableCollection(Of CandleModel)
        Private _currentOpenEpoch As Long = 0
        Private _currentSymbol As String = ""
        Private _currentTimeframe As String = ""

        ' ===== Gráfico y ajustes =====
        Private _settings As ChartSettings
        Private _suppressColorChange As Boolean = False
        Private _chartCanvas As CandleChart

        ' ===== Persistencia =====
        Private _repo            As CandleRepository
        Private _descargaService As DescargaService

        ' ===== Credenciales (cargadas desde BD) =====
        Private _appIdVirtual As String = ""
        Private _tokenVirtual As String = ""
        Private _appIdReal As String = ""
        Private _tokenReal As String = ""
        Private _activeToken As String = ""

        Private ReadOnly TIMEFRAME_MAP As New Dictionary(Of String, Integer) From {
            {"1m", 60}, {"5m", 300}, {"15m", 900}, {"1h", 3600}, {"1d", 86400}
        }

        ' ===== Trading Live (Tab Trading Live) =====
        Private _tradingWs As ClientWebSocket
        Private _tradingCts As CancellationTokenSource
        Private _tradingOperaciones As New ObservableCollection(Of OperacionModel)()
        Private _tradingClienteActual As ClientModel
        Private _tradingCredsActual As ClientDerivCredentials
        Private _pendingProposalAmount As Double = 0
        Private _pendingProposalId As String = ""
        Private _modoAvanzado As Boolean = False
        Private _tradingEsDemo As Boolean = True   ' guardado para reconexión automática

        ' ===== API Server embebido (Tab Server) =====
        Private _apiServer As TradingApiServer
        Private _pendingApiTrade As System.Threading.Tasks.TaskCompletionSource(Of JObject)
        Private _pendingApiSell As System.Threading.Tasks.TaskCompletionSource(Of JObject)

        ' ===================================================
        '  CONSTRUCTOR
        ' ===================================================
        Public Sub New()
            InitializeComponent()

            _candles = New ObservableCollection(Of CandleModel)()
            gridCandles.ItemsSource = _candles

            _chartCanvas = New CandleChart()
            _chartCanvas.Width = Double.NaN
            _chartCanvas.Height = Double.NaN
            CType(chartHost, System.Windows.Controls.Canvas).Children.Add(_chartCanvas)
            System.Windows.Controls.Canvas.SetLeft(_chartCanvas, 0)
            System.Windows.Controls.Canvas.SetTop(_chartCanvas, 0)
            AddHandler chartHost.SizeChanged, AddressOf SyncChartSize

            _settings = SettingsManager.GetChart()
            ApplySettingsToUI()
            _chartCanvas.SetData(_candles, _settings)

            Dim dbConfig = ConfigManager.LoadDbConfig()
            _repo            = New CandleRepository(dbConfig.GetConnectionString())
            _descargaService = New DescargaService(_repo)

            txtHistSymbol.Text = "R_100"
            dpDesde.SelectedDate = DateTime.Today.AddDays(-30)
            dpHasta.SelectedDate = DateTime.Today

            CargarAppIdDeriv()
            CargarClientesTrading()
        End Sub

        Protected Overrides Sub OnSourceInitialized(e As EventArgs)
            MyBase.OnSourceInitialized(e)
            ThemeHelper.ApplyDarkTitleBar(Me)
        End Sub

        ''' <summary>Carga App ID y tokens de Deriv del cliente Alexander desde BD.</summary>
        Private Async Sub CargarAppIdDeriv()
            Try
                Dim clientRepo As New ClientRepository()
                Dim clientes = Await clientRepo.GetAllAsync()
                Dim alexander = clientes.FirstOrDefault(
                    Function(c) c.Name.ToLower().Contains("alexander"))

                If alexander IsNot Nothing Then
                    Dim creds = Await clientRepo.GetDerivCredentialsAsync(alexander.Id)
                    If creds IsNot Nothing Then
                        _tokenReal = creds.ApiTokenReal
                        _tokenVirtual = creds.ApiTokenVirtual
                        _activeToken = If(Not String.IsNullOrWhiteSpace(_tokenVirtual), _tokenVirtual, _tokenReal)
                    End If
                End If
            Catch
            End Try
        End Sub

        ' ===================================================
        '  LIFECYCLE DE LA VENTANA
        ' ===================================================
        Private Sub Window_Closing(sender As Object, e As System.ComponentModel.CancelEventArgs) Handles Me.Closing
            If _cancellationTokenSource IsNot Nothing Then _cancellationTokenSource.Cancel()
            If _descargaService IsNot Nothing Then _descargaService.Detener()
            If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
            If _apiServer IsNot Nothing AndAlso _apiServer.IsRunning Then _apiServer.Stop()
        End Sub

        Private Sub tabMain_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs) Handles tabMain.SelectionChanged
            If tabChart IsNot Nothing AndAlso tabChart.IsSelected Then
                _chartCanvas.SetData(_candles, _settings)
            End If
        End Sub

        ' ===================================================
        '  HELPERS COMPARTIDOS (usados por múltiples tabs)
        ' ===================================================
        Private Sub RefreshChart()
            If tabChart IsNot Nothing AndAlso tabChart.IsSelected Then
                _chartCanvas.Redraw()
            End If
        End Sub

        Private Sub GoToBottom()
            If gridCandles.Items.Count > 0 Then
                gridCandles.ScrollIntoView(gridCandles.Items(gridCandles.Items.Count - 1))
            End If
        End Sub

        Private Shared Function EpochToString(epoch As Long) As String
            Dim base As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Return base.AddSeconds(epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        End Function

    End Class
End Namespace
