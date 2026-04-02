Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB
    Partial Public Class MainWindow
        Inherits System.Windows.Window

        ' ===== Constantes y config =====
        ' La URI se construye dinámicamente con el app_id del cliente desde la BD
        Private _apiUri As String = "wss://ws.derivws.com/websockets/v3?app_id=1089"
        Private Const PLATFORM As String = "Deriv"

        ' ===== Streaming =====
        Private _webSocket As ClientWebSocket
        Private _cancellationTokenSource As CancellationTokenSource
        Private _candles As ObservableCollection(Of CandleModel)
        Private _currentOpenEpoch As Long = 0
        Private _currentSymbol As String = ""
        Private _currentTimeframe As String = ""

        ' ===== Grafico y ajustes =====
        Private _settings As ChartSettings
        Private _suppressColorChange As Boolean = False
        Private _chartCanvas As CandleChart

        ' ===== Persistencia =====
        Private _repo As CandleRepository
        Private _downloader As HistoricalDownloader

        Private ReadOnly TIMEFRAME_MAP As New Dictionary(Of String, Integer) From {
            {"1m", 60}, {"5m", 300}, {"15m", 900}, {"1h", 3600}, {"1d", 86400}
        }

        ' ===================================================
        '  CONSTRUCTOR
        ' ===================================================
        Public Sub New()
            InitializeComponent()

            ' Coleccion de velas para streaming
            _candles = New ObservableCollection(Of CandleModel)()
            gridCandles.ItemsSource = _candles

            ' Grafico de velas canvas
            _chartCanvas = New CandleChart()
            _chartCanvas.Width = Double.NaN
            _chartCanvas.Height = Double.NaN
            CType(chartHost, System.Windows.Controls.Canvas).Children.Add(_chartCanvas)
            System.Windows.Controls.Canvas.SetLeft(_chartCanvas, 0)
            System.Windows.Controls.Canvas.SetTop(_chartCanvas, 0)
            AddHandler chartHost.SizeChanged, AddressOf SyncChartSize

            ' Configuracion de colores
            _settings = SettingsManager.GetChart()
            ApplySettingsToUI()
            _chartCanvas.SetData(_candles, _settings)

            ' Repositorio PostgreSQL — usa la config guardada en db_config.json
            Dim dbConfig = ConfigManager.LoadDbConfig()
            _repo = New CandleRepository(dbConfig.GetConnectionString())

            ' Descargador historico
            _downloader = New HistoricalDownloader(_repo)

            ' Valores iniciales del Tab de descarga
            txtHistSymbol.Text = "R_100"
            dpDesde.SelectedDate = DateTime.Today.AddDays(-30)
            dpHasta.SelectedDate = DateTime.Today

            ' Cargar app_id de Deriv desde las credenciales del cliente Alexander en BD
            CargarAppIdDeriv()
        End Sub

        ''' <summary>
        ''' Busca en la BD el cliente Alexander y carga su App ID real de Deriv.
        ''' Si no lo encuentra, usa el app_id por defecto (1089).
        ''' </summary>
        Private Async Sub CargarAppIdDeriv()
            Try
                Dim clientRepo As New ClientRepository()
                Dim clientes = Await clientRepo.GetAllAsync()
                Dim alexander = clientes.FirstOrDefault(
                    Function(c) c.Name.ToLower().Contains("alexander"))

                If alexander IsNot Nothing Then
                    Dim creds = Await clientRepo.GetDerivCredentialsAsync(alexander.Id)
                    If creds IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(creds.AppIdReal) Then
                        _apiUri = $"wss://ws.derivws.com/websockets/v3?app_id={creds.AppIdReal}"
                    End If
                End If
            Catch
                ' Si falla la carga, se mantiene el app_id por defecto (1089)
            End Try
        End Sub

        ' ===================================================
        '  TAB GRAFICO - Configuracion de Colores
        ' ===================================================
        Private Sub ApplySettingsToUI()
            _suppressColorChange = True
            txtBullColor.Text = _settings.BullishColor
            txtBearColor.Text = _settings.BearishColor
            txtWickColor.Text = _settings.WickColor
            txtBgColor.Text = _settings.BackgroundColor
            UpdateColorPreviews()
            _suppressColorChange = False
        End Sub

        Private Sub UpdateColorPreviews()
            SetPreviewColor(previewBull, txtBullColor.Text)
            SetPreviewColor(previewBear, txtBearColor.Text)
            SetPreviewColor(previewWick, txtWickColor.Text)
        End Sub

        Private Shared Sub SetPreviewColor(border As System.Windows.Controls.Border, hex As String)
            Try
                Dim c As Color = CType(ColorConverter.ConvertFromString(hex), Color)
                border.Background = New SolidColorBrush(c)
            Catch
                border.Background = New SolidColorBrush(Colors.Gray)
            End Try
        End Sub

        Private Sub ColorTextBox_Changed(sender As Object, e As System.Windows.Controls.TextChangedEventArgs)
            If _suppressColorChange Then Return
            UpdateColorPreviews()
        End Sub

        Private Sub btnSaveColors_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            _settings.BullishColor = txtBullColor.Text.Trim()
            _settings.BearishColor = txtBearColor.Text.Trim()
            _settings.WickColor = txtWickColor.Text.Trim()
            _settings.BackgroundColor = txtBgColor.Text.Trim()
            Dim all = SettingsManager.Load()
            all.Chart = _settings
            SettingsManager.Save(all)
            _chartCanvas.SetData(_candles, _settings)
            System.Windows.MessageBox.Show("Configuracion guardada.", "OK", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information)
        End Sub

        Private Sub ChartHost_SizeChanged(sender As Object, e As System.Windows.SizeChangedEventArgs)
            _chartCanvas.Redraw()
        End Sub

        Private Sub SyncChartSize(sender As Object, e As System.Windows.SizeChangedEventArgs)
            _chartCanvas.Width = chartHost.ActualWidth
            _chartCanvas.Height = chartHost.ActualHeight
            _chartCanvas.Redraw()
        End Sub

        ' ===================================================
        '  TAB STREAMING - Conexion WebSocket
        ' ===================================================
        Private Async Sub btnConnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            btnConnect.IsEnabled = False
            btnDisconnect.IsEnabled = True
            txtSymbol.IsEnabled = False
            cmbTimeframe.IsEnabled = False

            Dim symbol As String = txtSymbol.Text.Trim()
            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframeStr As String = tfItem.Content.ToString()
            Dim granularity As Integer = TIMEFRAME_MAP(timeframeStr)

            _currentSymbol = symbol
            _currentTimeframe = timeframeStr
            _candles.Clear()
            _currentOpenEpoch = 0
            txtStatus.Text = String.Format("Conectando a {0} ({1})...", symbol, timeframeStr)

            _cancellationTokenSource = New CancellationTokenSource()
            _webSocket = New ClientWebSocket()

            Try
                Await _webSocket.ConnectAsync(New Uri(_apiUri), _cancellationTokenSource.Token)
                txtStatus.Text = "Conectado. Solicitando historial..."

                Dim reqJson As String = BuildRequestJson(symbol, granularity)
                Dim reqBytes As Byte() = Encoding.UTF8.GetBytes(reqJson)
                Await _webSocket.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, _cancellationTokenSource.Token)

                Task.Run(Function() As Task
                             Return ListenLoop()
                         End Function)
            Catch ex As Exception
                System.Windows.MessageBox.Show("Error de conexion: " & ex.Message)
                ResetUI()
            End Try
        End Sub

        Private Async Sub btnDisconnect_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _cancellationTokenSource IsNot Nothing Then _cancellationTokenSource.Cancel()
            If _webSocket IsNot Nothing AndAlso _webSocket.State = WebSocketState.Open Then
                Try
                    Await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "OK", CancellationToken.None)
                Catch
                End Try
            End If
            Dispatcher.Invoke(AddressOf ResetUI)
        End Sub

        Private Sub ResetUI()
            btnConnect.IsEnabled = True
            btnDisconnect.IsEnabled = False
            txtSymbol.IsEnabled = True
            cmbTimeframe.IsEnabled = True
            txtStatus.Text = "Desconectado"
        End Sub

        Private Shared Function BuildRequestJson(symbol As String, granularity As Integer) As String
            Dim json As New JObject()
            json.Add("ticks_history", New JValue(symbol))
            json.Add("adjust_start_time", New JValue(1))
            json.Add("count", New JValue(50))
            json.Add("style", New JValue("candles"))
            json.Add("end", New JValue("latest"))
            json.Add("granularity", New JValue(granularity))
            json.Add("subscribe", New JValue(1))
            Return json.ToString()
        End Function

        ' ===================================================
        '  BUCLE DE ESCUCHA STREAMING
        ' ===================================================
        Private Async Function ListenLoop() As Task
            Dim buffer(16383) As Byte
            Try
                Do While _webSocket.State = WebSocketState.Open AndAlso Not _cancellationTokenSource.IsCancellationRequested
                    Dim sb As New StringBuilder()
                    Dim result As WebSocketReceiveResult
                    Do
                        result = Await _webSocket.ReceiveAsync(New ArraySegment(Of Byte)(buffer), _cancellationTokenSource.Token)
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                    Loop While Not result.EndOfMessage

                    If result.MessageType = WebSocketMessageType.Close Then
                        Dispatcher.Invoke(AddressOf ResetUI)
                        Exit Do
                    End If

                    Dim data As JObject = JObject.Parse(sb.ToString())

                    If data.ContainsKey("error") Then
                        Dim errMsg As String = data("error")("message").ToString()
                        Dispatcher.Invoke(Sub()
                                              System.Windows.MessageBox.Show("Error API: " & errMsg)
                                              ResetUI()
                                          End Sub)
                        Exit Do
                    End If

                    If data.ContainsKey("candles") Then
                        Dim arr As JArray = CType(data("candles"), JArray)
                        Dispatcher.Invoke(Sub() HandleHistoricalCandles(arr))
                    ElseIf data.ContainsKey("ohlc") Then
                        Dim ohlcObj As JObject = CType(data("ohlc"), JObject)
                        Dispatcher.Invoke(Sub() HandleLiveTick(ohlcObj))
                    End If
                Loop
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If Not _cancellationTokenSource.IsCancellationRequested Then
                    Dispatcher.Invoke(Sub()
                                          System.Windows.MessageBox.Show("Desconectado: " & ex.Message)
                                          ResetUI()
                                      End Sub)
                End If
            End Try
        End Function

        ' ===================================================
        '  PROCESAR DATOS DE STREAMING
        ' ===================================================
        Private Sub HandleHistoricalCandles(arr As JArray)
            Dim total As Integer = arr.Count
            If total = 0 Then Return

            Dim idx As Integer = 0
            Do While idx <= total - 2
                Dim item As JObject = CType(arr(idx), JObject)
                Dim m As New CandleModel()
                m.Epoch = CLng(item("epoch"))
                m.OpenTime = EpochToString(m.Epoch)
                m.OpenPrice = CDec(item("open"))
                m.High = CDec(item("high"))
                m.Low = CDec(item("low"))
                m.ClosePrice = CDec(item("close"))
                m.Status = "[Cerrada]"
                _candles.Add(m)
                idx += 1
            Loop

            Dim last As JObject = CType(arr(total - 1), JObject)
            Dim live As New CandleModel()
            live.Epoch = CLng(last("epoch"))
            live.OpenTime = EpochToString(live.Epoch)
            live.OpenPrice = CDec(last("open"))
            live.High = CDec(last("high"))
            live.Low = CDec(last("low"))
            live.ClosePrice = CDec(last("close"))
            live.Status = "Actualizandose..."
            _candles.Add(live)
            _currentOpenEpoch = live.Epoch

            txtStatus.Text = String.Format("En vivo: {0} ({1})", _currentSymbol, _currentTimeframe)
            RefreshChart()
            GoToBottom()

            ' Guardar historial en BD (fire & forget)
            Dim closedCandles As New List(Of CandleModel)(_candles.Take(_candles.Count - 1))
            Dim sym As String = _currentSymbol
            Dim tf As String = _currentTimeframe
            Task.Run(Function() As Task
                         Return _repo.SaveBatchAsync(PLATFORM, sym, tf, closedCandles, isClosed:=True)
                     End Function)
        End Sub

        Private Sub HandleLiveTick(ohlcObj As JObject)
            Dim tickEpoch As Long = CLng(ohlcObj("open_time"))
            Dim isNewCandle As Boolean = (tickEpoch <> _currentOpenEpoch)

            If isNewCandle Then
                If _candles.Count > 0 Then
                    _candles(_candles.Count - 1).Status = "[Cerrada]"
                End If
                Dim m As New CandleModel()
                m.Epoch = tickEpoch
                m.OpenTime = EpochToString(tickEpoch)
                m.OpenPrice = CDec(ohlcObj("open"))
                m.High = CDec(ohlcObj("high"))
                m.Low = CDec(ohlcObj("low"))
                m.ClosePrice = CDec(ohlcObj("close"))
                m.Status = "Actualizandose..."
                _candles.Add(m)
                _currentOpenEpoch = tickEpoch
                If _candles.Count > 60 Then _candles.RemoveAt(0)
            Else
                If _candles.Count > 0 Then
                    Dim live As CandleModel = _candles(_candles.Count - 1)
                    live.High = CDec(ohlcObj("high"))
                    live.Low = CDec(ohlcObj("low"))
                    live.ClosePrice = CDec(ohlcObj("close"))
                End If
            End If

            RefreshChart()
            GoToBottom()

            ' Guardar vela cerrada en BD si hubo cambio de periodo
            If isNewCandle AndAlso _candles.Count >= 2 Then
                Dim closedCandle As CandleModel = _candles(_candles.Count - 2)
                Dim sym As String = _currentSymbol
                Dim tf As String = _currentTimeframe
                Dim snap As CandleModel = closedCandle
                Task.Run(Function() As Task
                             Return _repo.SaveCandleAsync(PLATFORM, sym, tf, snap, isClosed:=True)
                         End Function)
            End If
        End Sub

        ' ===================================================
        '  TAB DESCARGA HISTORICA
        ' ===================================================
        Private Async Sub btnDescargar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim symbol As String = txtHistSymbol.Text.Trim()
            If String.IsNullOrEmpty(symbol) Then
                System.Windows.MessageBox.Show("Ingresa un simbolo valido (ej: R_100).")
                Return
            End If

            If Not dpDesde.SelectedDate.HasValue OrElse Not dpHasta.SelectedDate.HasValue Then
                System.Windows.MessageBox.Show("Selecciona las fechas Desde y Hasta.")
                Return
            End If

            Dim fromDate As DateTime = dpDesde.SelectedDate.Value
            Dim toDate As DateTime = dpHasta.SelectedDate.Value

            If fromDate >= toDate Then
                System.Windows.MessageBox.Show("La fecha Desde debe ser anterior a Hasta.")
                Return
            End If

            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframe As String = tfItem.Content.ToString()

            ' Limpiar log y resetear UI
            txtDescargaLog.Text = ""
            txtDescargaStatus.Text = String.Format("Iniciando descarga de {0} {1}...", symbol, timeframe)
            txtDescargaContador.Text = ""
            pbDescarga.IsIndeterminate = True
            pbDescarga.Value = 0
            btnDescargar.IsEnabled = False
            btnDetenerDescarga.IsEnabled = True

            AddLogLine(String.Format("=== Descarga iniciada: {0} | {1} | {2} → {3} ===",
                       symbol, timeframe,
                       fromDate.ToString("yyyy-MM-dd"),
                       toDate.ToString("yyyy-MM-dd")))

            Dim sym As String = symbol
            Dim tf As String = timeframe
            Dim fd As DateTime = fromDate
            Dim td As DateTime = toDate

            Await Task.Run(Function() As Task
                               Return _downloader.DownloadAsync(PLATFORM, sym, tf, fd, td,
                                   Sub(progress As DownloadProgress)
                                       Dispatcher.Invoke(Sub() UpdateDownloadProgress(progress))
                                   End Sub)
                           End Function)
        End Sub

        Private Sub btnDetenerDescarga_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            _downloader.StopDownload()
            txtDescargaStatus.Text = "Deteniendo descarga..."
            btnDetenerDescarga.IsEnabled = False
        End Sub

        Private Async Sub btnCalcularIndicadores_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim symbol As String = txtHistSymbol.Text.Trim()
            If String.IsNullOrEmpty(symbol) Then
                System.Windows.MessageBox.Show("Ingresa un simbolo (ej: R_100).")
                Return
            End If

            Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim timeframe As String = tfItem.Content.ToString()

            Dim period As Integer = 20
            If Not Integer.TryParse(txtMaPeriod.Text.Trim(), period) OrElse period < 2 Then
                System.Windows.MessageBox.Show("El periodo debe ser un numero entero >= 2.")
                Return
            End If

            AppLogger.Info(String.Format("btnCalcularIndicadores: {0} {1} period={2}", symbol, timeframe, period))

            txtDescargaStatus.Text = String.Format("Cargando velas desde BD: {0} {1}...", symbol, timeframe)
            txtDescargaContador.Text = ""
            btnCalcularIndicadores.IsEnabled = False
            txtDescargaLog.Text = ""
            pbDescarga.IsIndeterminate = True

            Dim sym As String = symbol
            Dim tf As String = timeframe
            Dim per As Integer = period

            Try
                Await Task.Run(Async Function() As Task
                    Try
                        AppLogger.Info("Cargando velas desde PostgreSQL...")
                        Dim candles As List(Of CandleModel) = Await _repo.GetCandlesAsync(PLATFORM, sym, tf, limit:=500)
                        AppLogger.Info(String.Format("Velas cargadas: {0}", candles.Count))

                        Dispatcher.Invoke(Sub()
                            Try
                                pbDescarga.IsIndeterminate = False

                                If candles.Count = 0 Then
                                    txtDescargaStatus.Text = "Sin datos en BD para " & sym & " " & tf & ". Descarga historial primero."
                                    btnCalcularIndicadores.IsEnabled = True
                                    Return
                                End If

                                Dim sb As New System.Text.StringBuilder()
                                Dim showRows As Integer = 15
                                Dim showFrom As Integer = Math.Max(0, candles.Count - showRows)
                                Dim line70 As String = New String("="c, 70)
                                Dim dash70 As String = New String("-"c, 70)

                                sb.AppendLine(line70)
                                sb.AppendLine(String.Format(" INDICADORES: {0} | {1} | Período MA: {2} | {3} velas de BD", sym, tf, per, candles.Count))
                                sb.AppendLine(line70)

                                ' ── 1. Medias Moviles ────────────────────
                                AppLogger.Info("Calculando Medias Moviles...")
                                sb.AppendLine()
                                sb.AppendLine(String.Format("── TREND: Medias Moviles ({0}) ──", per))
                                sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                                           "Hora", "Close", "SMA", "EMA", "WMA"))
                                sb.AppendLine(dash70)

                                Dim smaR = Indicators.MovingAverages.SMA(candles, per)
                                Dim emaR = Indicators.MovingAverages.EMA(candles, per)
                                Dim wmaR = Indicators.MovingAverages.WMA(candles, per)

                                Dim idx As Integer = showFrom
                                Do While idx < candles.Count
                                    sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(smaR(idx).IndicatorValue, "F4"),
                                        FormatVal(emaR(idx).IndicatorValue, "F4"),
                                        FormatVal(wmaR(idx).IndicatorValue, "F4")))
                                    idx += 1
                                Loop

                                ' ── 2. RSI ───────────────────────────────
                                AppLogger.Info("Calculando RSI(14)...")
                                Dim rsiIndicator As New Indicators.RSI(14)
                                Dim rsiR = rsiIndicator.Calculate(candles)
                                sb.AppendLine()
                                sb.AppendLine("── MOMENTUM: RSI(14) ──")
                                sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-20}",
                                                           "Hora", "Close", "RSI", "Señal"))
                                sb.AppendLine(dash70)
                                idx = showFrom
                                Do While idx < candles.Count
                                    Dim rsiVal As String = FormatVal(rsiR(idx).IndicatorValue, "F2")
                                    Dim signal As String = ""
                                    If rsiR(idx).IndicatorValue.HasValue Then
                                        Dim rv As Decimal = rsiR(idx).IndicatorValue.Value
                                        If rv > 70 Then signal = "SOBRECOMPRA"
                                        If rv < 30 Then signal = "SOBREVENTA"
                                        If rv >= 45 AndAlso rv <= 55 Then signal = "NEUTRAL"
                                    End If
                                    sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-20}",
                                        candles(idx).OpenTime, candles(idx).ClosePrice.ToString("F4"),
                                        rsiVal, signal))
                                    idx += 1
                                Loop

                                ' ── 3. Bollinger Bands ──────────────────
                                AppLogger.Info("Calculando Bollinger Bands(20,2)...")
                                Dim bbIndicator As New Indicators.BollingerBands(20, 2D)
                                Dim bbR = bbIndicator.CalculateBands(candles)
                                sb.AppendLine()
                                sb.AppendLine("── VOLATILITY: BB(20,2) ──")
                                sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12} {5,-10}",
                                                           "Hora", "Close", "Upper", "Middle", "Lower", "BandW%"))
                                sb.AppendLine(dash70)
                                idx = showFrom
                                Do While idx < candles.Count
                                    sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12} {5,-10}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(bbR(idx).Upper, "F4"),
                                        FormatVal(bbR(idx).Middle, "F4"),
                                        FormatVal(bbR(idx).Lower, "F4"),
                                        FormatVal(bbR(idx).BandWidth, "F2")))
                                    idx += 1
                                Loop

                                ' ── 4. MACD ─────────────────────────────
                                AppLogger.Info("Calculando MACD(12,26,9)...")
                                Dim macdIndicator As New Indicators.MACD(12, 26, 9)
                                Dim macdR = macdIndicator.CalculateMacd(candles)
                                sb.AppendLine()
                                sb.AppendLine("── TREND: MACD(12,26,9) ──")
                                sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                                           "Hora", "Close", "MACD", "Signal", "Histogram"))
                                sb.AppendLine(dash70)
                                idx = showFrom
                                Do While idx < candles.Count
                                    Dim hist As String = FormatVal(macdR(idx).Histogram, "F5")
                                    If macdR(idx).Histogram.HasValue Then
                                        hist = (If(macdR(idx).Histogram.Value > 0, "+", "")) & hist
                                    End If
                                    sb.AppendLine(String.Format("{0,-19} {1,-12} {2,-12} {3,-12} {4,-12}",
                                        candles(idx).OpenTime,
                                        candles(idx).ClosePrice.ToString("F4"),
                                        FormatVal(macdR(idx).MacdLine, "F5"),
                                        FormatVal(macdR(idx).SignalLine, "F5"),
                                        hist))
                                    idx += 1
                                Loop

                                sb.AppendLine()
                                sb.AppendLine(line70)
                                sb.AppendLine(String.Format("Log completo en: {0}", AppLogger.GetLogPath()))

                                txtDescargaLog.Text = sb.ToString()
                                txtDescargaStatus.Text = String.Format(
                                    "Completo: {0} velas | SMA/EMA/WMA({1}) | RSI(14) | BB(20,2) | MACD(12,26,9)", candles.Count, per)
                                txtDescargaContador.Text = String.Format("| Último: {0}", candles.Last().OpenTime)
                                pbDescarga.Value = 100
                                btnCalcularIndicadores.IsEnabled = True
                                AppLogger.Info("Calculo de indicadores completado OK")

                            Catch exInner As Exception
                                AppLogger.Error("Error dentro de Dispatcher.Invoke en calculo de indicadores", exInner)
                                pbDescarga.IsIndeterminate = False
                                txtDescargaStatus.Text = "Error al calcular. Ver log: " & AppLogger.GetLogPath()
                                btnCalcularIndicadores.IsEnabled = True
                            End Try
                        End Sub)

                    Catch exTask As Exception
                        AppLogger.Error("Error en Task.Run de calculo de indicadores", exTask)
                        Dispatcher.Invoke(Sub()
                            pbDescarga.IsIndeterminate = False
                            txtDescargaStatus.Text = "Error. Revisa log: " & AppLogger.GetLogPath()
                            btnCalcularIndicadores.IsEnabled = True
                        End Sub)
                    End Try
                End Function)

            Catch exOuter As Exception
                AppLogger.Error("Error inesperado en btnCalcularIndicadores_Click", exOuter)
                pbDescarga.IsIndeterminate = False
                txtDescargaStatus.Text = "Error inesperado. Revisa log: " & AppLogger.GetLogPath()
                btnCalcularIndicadores.IsEnabled = True
            End Try
        End Sub

        ''' <summary>Formatea un valor nullable a string con formato o N/A si es Nothing.</summary>
        Private Shared Function FormatVal(v As Nullable(Of Decimal), fmt As String) As String
            If v.HasValue Then Return v.Value.ToString(fmt)
            Return "N/A"
        End Function

        Private Sub UpdateDownloadProgress(progress As DownloadProgress)
            txtDescargaStatus.Text = progress.StatusMsg

            If progress.PageNum > 0 Then
                txtDescargaContador.Text = String.Format("| Pagina {0} | Total: {1} velas",
                                                          progress.PageNum, progress.TotalSaved)
            End If

            AddLogLine(String.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), progress.StatusMsg))

            If progress.IsComplete Then
                pbDescarga.IsIndeterminate = False
                pbDescarga.Value = 100

                If progress.HasError Then
                    pbDescarga.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#F38BA8"), Color))
                Else
                    pbDescarga.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#A6E3A1"), Color))
                End If

                btnDescargar.IsEnabled = True
                btnDetenerDescarga.IsEnabled = False

                ' Consultar conteo final en BD
                Dim sym As String = txtHistSymbol.Text.Trim()
                Dim tfItem As System.Windows.Controls.ComboBoxItem = CType(cmbHistTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
                Dim tf As String = tfItem.Content.ToString()
                Task.Run(Async Function() As Task
                             Dim count As Long = Await _repo.CountAsync(PLATFORM, sym, tf)
                             Dispatcher.Invoke(Sub()
                                                   AddLogLine(String.Format("--- BD: {0} velas totales en {1} {2} ---", count, sym, tf))
                                               End Sub)
                         End Function)
            End If
        End Sub

        Private Sub AddLogLine(text As String)
            If txtDescargaLog.Text.Length > 0 Then
                txtDescargaLog.Text = text & Environment.NewLine & txtDescargaLog.Text
            Else
                txtDescargaLog.Text = text
            End If
        End Sub

        ' ===================================================
        '  HELPERS GENERALES
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

        Private Sub Window_Closing(sender As Object, e As System.ComponentModel.CancelEventArgs) Handles Me.Closing
            If _cancellationTokenSource IsNot Nothing Then _cancellationTokenSource.Cancel()
            If _downloader IsNot Nothing Then _downloader.StopDownload()
        End Sub

        Private Sub tabMain_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs) Handles tabMain.SelectionChanged
            If tabChart IsNot Nothing AndAlso tabChart.IsSelected Then
                _chartCanvas.SetData(_candles, _settings)
            End If
        End Sub
    End Class
End Namespace
