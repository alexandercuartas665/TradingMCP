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

        ' ===== Trading / Credenciales =====
        Private _appIdVirtual As String = ""
        Private _tokenVirtual As String = ""
        Private _appIdReal As String = ""
        Private _tokenReal As String = ""
        Private _activeToken As String = ""

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

            ' Cargar clientes en el combo de Trading
            CargarClientesTrading()
        End Sub

        Protected Overrides Sub OnSourceInitialized(e As EventArgs)
            MyBase.OnSourceInitialized(e)
            ThemeHelper.ApplyDarkTitleBar(Me)
        End Sub


        ''' <summary>
        ''' Busca en la BD el cliente y carga su App ID y Tokens de Deriv.
        ''' </summary>
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

                        ' El streaming usa siempre app_id=1089 (API pública de velas, no requiere auth).
                        ' El OAuth App ID es solo para el nuevo flujo de Trading Live.
                        _activeToken = If(Not String.IsNullOrWhiteSpace(_tokenVirtual), _tokenVirtual, _tokenReal)
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

                ' ticks_history es dato público — no requiere authorize.
                ' La autenticación solo aplica al tab Trading Live (nuevo flujo OTP).
                txtStatus.Text = "Conectado. Solicitando historial..."

                Dim reqJson As String = BuildRequestJson(symbol, granularity)
                Dim reqBytes As Byte() = Encoding.UTF8.GetBytes(reqJson)
                Await _webSocket.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, _cancellationTokenSource.Token)

#Disable Warning BC42358
                Task.Run(Function() As Task
                             Return ListenLoop()
                         End Function)
#Enable Warning BC42358
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
        '  TRADING CONTROLS
        ' ===================================================
        Private Async Sub btnBuyCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await SendTradeRequest("CALL")
        End Sub

        Private Async Sub btnSellPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await SendTradeRequest("PUT")
        End Sub

        Private Async Function SendTradeRequest(contractType As String) As Task
            ' Usa el WebSocket del tab Trading Live si está conectado
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                txtTradeResult.Text = "⚡ Conecta una cuenta en el tab 'Trading Live' para operar."
                txtTradeResult.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
                Return
            End If

            Dim symbol As String = txtTradeSymbol.Text.Trim()
            Dim amount As Double
            Dim duration As Integer

            If Not Double.TryParse(txtTradeAmount.Text.Trim(), amount) OrElse amount <= 0 Then
                txtTradeResult.Text = "❌ Monto inválido."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                Return
            End If
            If Not Integer.TryParse(txtTradeDuration.Text.Trim(), duration) OrElse duration <= 0 Then
                txtTradeResult.Text = "❌ Duración inválida."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                Return
            End If

            txtTradeResult.Text = "⏳ Solicitando cotización " & contractType & " " & symbol & "..."
            txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)

            Try
                _pendingProposalAmount = amount
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", amount)
                req.Add("basis", "stake")
                req.Add("contract_type", contractType)
                req.Add("currency", "USD")
                req.Add("duration", duration)
                req.Add("duration_unit", "t")
                req.Add("underlying_symbol", symbol)

                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)

                txtTradeResult.Text = "⏳ Esperando cotización..."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)
            Catch ex As Exception
                txtTradeResult.Text = "❌ Error: " & ex.Message
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
            End Try
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
                        Dim errCode As String = If(data("error")("code") IsNot Nothing, data("error")("code").ToString(), "")
                        ' Errores que cortan el streaming (conexión inválida)
                        If errCode = "InvalidSymbol" OrElse errCode = "MarketIsClosed" Then
                            Dispatcher.Invoke(Sub()
                                                  System.Windows.MessageBox.Show("Error API: " & errMsg, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                                                  ResetUI()
                                              End Sub)
                            Exit Do
                        Else
                            ' Errores no críticos (trade, auth) — solo mostrar en status, no cortar
                            Dispatcher.Invoke(Sub()
                                                  txtTradeResult.Text = "❌ " & errMsg
                                                  txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                                              End Sub)
                        End If
                    End If

                    If data.ContainsKey("candles") Then
                        Dim arr As JArray = CType(data("candles"), JArray)
                        Dispatcher.Invoke(Sub() HandleHistoricalCandles(arr))
                    ElseIf data.ContainsKey("ohlc") Then
                        Dim ohlcObj As JObject = CType(data("ohlc"), JObject)
                        Dispatcher.Invoke(Sub() HandleLiveTick(ohlcObj))
                    ElseIf data.ContainsKey("authorize") Then
                        Dispatcher.Invoke(Sub()
                                              txtTradeResult.Text = "✅ Cuenta autorizada para operar."
                                              txtTradeResult.Foreground = New SolidColorBrush(Colors.LightGreen)
                                          End Sub)
                    ElseIf data.ContainsKey("buy") Then
                        Dispatcher.Invoke(Sub()
                                              Dim buyObj As JObject = CType(data("buy"), JObject)
                                              Dim transactionId As String = buyObj("transaction_id").ToString()
                                              Dim buyPrice As String = buyObj("buy_price").ToString()
                                              txtTradeResult.Text = $"✅ Operación exitosa! ID: {transactionId} - Precio: {buyPrice}"
                                              txtTradeResult.Foreground = New SolidColorBrush(Colors.LightGreen)
                                          End Sub)
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
            If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
        End Sub

        Private Sub tabMain_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs) Handles tabMain.SelectionChanged
            If tabChart IsNot Nothing AndAlso tabChart.IsSelected Then
                _chartCanvas.SetData(_candles, _settings)
            End If
        End Sub

        ' ===================================================
        '  TAB TRADING LIVE
        ' ===================================================
        Private _tradingWs As ClientWebSocket
        Private _tradingCts As CancellationTokenSource
        Private _tradingOperaciones As New System.Collections.ObjectModel.ObservableCollection(Of OperacionModel)()
        Private _tradingClienteActual As ClientModel
        Private _tradingCredsActual As ClientDerivCredentials
        ' Proposal pendiente
        Private _pendingProposalAmount As Double = 0
        Private _pendingProposalId As String = ""
        Private _modoAvanzado As Boolean = False

        Private Async Sub CargarClientesTrading()
            Try
                Dim repo As New ClientRepository()
                Dim lista = Await repo.GetAllAsync()
                cmbTradingClientes.ItemsSource = lista
                If lista.Count > 0 Then cmbTradingClientes.SelectedIndex = 0
            Catch ex As Exception
                txtTradingEstado.Text = "Error BD"
            End Try
        End Sub

        Private Async Sub cmbTradingClientes_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            _tradingClienteActual = TryCast(cmbTradingClientes.SelectedItem, ClientModel)
            If _tradingClienteActual Is Nothing Then Return
            Try
                Dim repo As New ClientRepository()
                _tradingCredsActual = Await repo.GetDerivCredentialsAsync(_tradingClienteActual.Id)
            Catch
            End Try
        End Sub

        Private Async Sub btnConectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingClienteActual Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona un cliente primero.")
                Return
            End If
            If _tradingCredsActual Is Nothing Then
                System.Windows.MessageBox.Show("El cliente no tiene credenciales Deriv configuradas.")
                Return
            End If

            Dim esDemo As Boolean = (cmbTradingTipoCuenta.SelectedIndex = 0)
            Dim accountId As String = If(esDemo, _tradingCredsActual.AccountIdVirtual, _tradingCredsActual.AccountIdReal)
            Dim appId As String = If(esDemo, _tradingCredsActual.AppIdVirtual, _tradingCredsActual.AppIdReal)
            Dim token As String = If(esDemo, _tradingCredsActual.ApiTokenVirtual, _tradingCredsActual.ApiTokenReal)

            If String.IsNullOrWhiteSpace(accountId) OrElse String.IsNullOrWhiteSpace(token) Then
                System.Windows.MessageBox.Show("Faltan Account ID o Token para la cuenta " & If(esDemo, "Demo", "Real") & ".")
                Return
            End If

            btnConectarTrading.IsEnabled = False
            txtTradingEstado.Text = "Conectando..."
            txtTradingEstado.Foreground = New SolidColorBrush(Colors.Yellow)
            txtTradingBalance.Text = "--"

            Try
                ' Paso 1: Obtener OTP via REST
                Dim wsUrl As String = Nothing
                Dim otpUrl = String.Format("https://api.derivws.com/trading/v1/options/accounts/{0}/otp", accountId)

                Dim responseBody As String = Await Task.Run(Function()
                                                                Using wc As New System.Net.WebClient()
                                                                    wc.Headers.Add("Authorization", "Bearer " & token)
                                                                    wc.Headers.Add("deriv-app-id", appId)
                                                                    Return wc.UploadString(otpUrl, "POST", "")
                                                                End Using
                                                            End Function)

                Dim otpObj = JObject.Parse(responseBody)
                If otpObj("data") IsNot Nothing AndAlso otpObj("data")("url") IsNot Nothing Then
                    wsUrl = otpObj("data")("url").ToString()
                Else
                    Throw New Exception("Respuesta OTP inesperada: " & responseBody)
                End If

                ' Paso 2: Conectar WebSocket con OTP
                If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
                _tradingCts = New CancellationTokenSource()
                _tradingWs = New ClientWebSocket()
                Await _tradingWs.ConnectAsync(New Uri(wsUrl), _tradingCts.Token)

                ' UI: conectado
                txtTradingEstado.Text = If(esDemo, "Demo", "Real") & " - Conectado"
                txtTradingEstado.Foreground = New SolidColorBrush(Colors.LightGreen)
                btnConectarTrading.IsEnabled = False
                btnDesconectarTrading.IsEnabled = True
                btnTradingCall.IsEnabled = True
                btnTradingPut.IsEnabled = True
                btnCotizar.IsEnabled = True

                ' Cargar historial de operaciones desde BD
                _tradingOperaciones.Clear()
                If _tradingClienteActual IsNot Nothing Then
                    Dim repo As New ClientRepository()
                    Dim historial = Await repo.ObtenerOperacionesAsync(_tradingClienteActual.Id)
                    For Each op In historial
                        _tradingOperaciones.Add(op)
                    Next
                End If
                gridOperaciones.ItemsSource = _tradingOperaciones

                ' Suscribir balance
                Await SuscribirBalanceTradingAsync()

                ' Iniciar escucha
#Disable Warning BC42358
                Task.Run(Function() EscucharTradingAsync())
#Enable Warning BC42358

            Catch ex As System.Net.WebException
                Dim errBody As String = ""
                If ex.Response IsNot Nothing Then
                    Using sr As New System.IO.StreamReader(ex.Response.GetResponseStream())
                        errBody = sr.ReadToEnd()
                    End Using
                End If
                txtTradingEstado.Text = "Error conexión"
                txtTradingEstado.Foreground = New SolidColorBrush(Colors.Red)
                btnConectarTrading.IsEnabled = True
                System.Windows.MessageBox.Show("Error al conectar:" & vbCrLf & errBody, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error)
            Catch ex As Exception
                txtTradingEstado.Text = "Error"
                txtTradingEstado.Foreground = New SolidColorBrush(Colors.Red)
                btnConectarTrading.IsEnabled = True
                System.Windows.MessageBox.Show("Error: " & ex.Message, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error)
            End Try
        End Sub

        Private Sub btnDesconectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
            txtTradingEstado.Text = "Desconectado"
            txtTradingEstado.Foreground = New SolidColorBrush(Colors.OrangeRed)
            txtTradingBalance.Text = "--"
            btnConectarTrading.IsEnabled = True
            btnDesconectarTrading.IsEnabled = False
            btnTradingCall.IsEnabled = False
            btnTradingPut.IsEnabled = False
            btnCotizar.IsEnabled = False
            btnEjecutarAvanzado.IsEnabled = False
        End Sub

        Private Async Function SuscribirBalanceTradingAsync() As Task
            Try
                Dim req As New JObject()
                req.Add("balance", 1)
                req.Add("subscribe", 1)
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch
            End Try
        End Function

        Private Async Sub btnTradingCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarContratoTradingAsync("CALL")
        End Sub

        Private Async Sub btnTradingPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarContratoTradingAsync("PUT")
        End Sub

        ' ---- Helpers para leer controles compartidos ----
        Private Function GetSymbol() As String
            Dim item = TryCast(cmbTradingSymbol.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "R_100")
        End Function
        Private Function GetDurUnit() As String
            Dim item = TryCast(cmbTradingDurUnit.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "t")
        End Function

        Private Async Function EjecutarContratoTradingAsync(tipo As String) As Task
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.")
                Return
            End If
            Dim symbol = GetSymbol()
            Dim monto As Double
            Dim duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            txtTradingOpEstado.Text = String.Format("⏳ Cotizando {0} {1} ${2}...", tipo, symbol, monto)
            Try
                _pendingProposalAmount = monto
                _modoAvanzado = False
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", monto)
                req.Add("basis", "stake")
                req.Add("contract_type", tipo)
                req.Add("currency", "USD")
                req.Add("duration", duracion)
                req.Add("duration_unit", GetDurUnit())
                req.Add("underlying_symbol", symbol)
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error: " & ex.Message
            End Try
        End Function

        ' ---- Panel Avanzado ----
        Private Sub expAvanzado_Expanded(sender As Object, e As System.Windows.RoutedEventArgs)
            btnTradingCall.IsEnabled = False
            btnTradingPut.IsEnabled = False
        End Sub
        Private Sub expAvanzado_Collapsed(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs IsNot Nothing AndAlso _tradingWs.State = WebSocketState.Open Then
                btnTradingCall.IsEnabled = True
                btnTradingPut.IsEnabled = True
            End If
            txtCotizacion.Text = "— presiona Cotizar primero —"
            _pendingProposalId = ""
            btnEjecutarAvanzado.IsEnabled = False
        End Sub

        Private Sub cmbTradingSymbol_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            ' Resetear cotización al cambiar símbolo
            If txtCotizacion IsNot Nothing Then
                txtCotizacion.Text = "— presiona Cotizar primero —"
                _pendingProposalId = ""
                If btnEjecutarAvanzado IsNot Nothing Then btnEjecutarAvanzado.IsEnabled = False
            End If
        End Sub

        Private Sub cmbContratoTipo_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            Dim item = TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim tipo = If(item?.Tag?.ToString(), "CALL")
            ' Resetear cotización
            If Not txtCotizacion Is Nothing Then
                txtCotizacion.Text = "— presiona Cotizar primero —"
                _pendingProposalId = ""
                btnEjecutarAvanzado.IsEnabled = False
                ' Mostrar/ocultar barrera según tipo
                Select Case tipo
                    Case "ONETOUCH", "NOTOUCH"
                        pnlBarrera.Visibility = System.Windows.Visibility.Visible
                        lblBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera.Text = "+1.5"
                        lblBarreraHint.Text = "Ej: +1.5 (relativa) o 628.50 (absoluta)"
                    Case "DIGITOVER", "DIGITUNDER", "DIGITMATCH", "DIGITDIFF"
                        pnlBarrera.Visibility = System.Windows.Visibility.Visible
                        lblBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera.Text = "5"
                        lblBarreraHint.Text = "Dígito del 0 al 9"
                    Case Else
                        pnlBarrera.Visibility = System.Windows.Visibility.Collapsed
                End Select
            End If
        End Sub

        Private Async Sub btnCotizar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.") : Return
            End If
            Dim tipoItem = TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim tipo = If(tipoItem?.Tag?.ToString(), "CALL")
            Dim basisItem = TryCast(cmbTradingBasis.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim basis = If(basisItem?.Tag?.ToString(), "stake")
            Dim currency = If(TryCast(cmbTradingCurrency.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), "USD")
            Dim symbol = GetSymbol()
            Dim monto As Double
            Dim duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            txtCotizacion.Text = "⏳ Cotizando..."
            btnEjecutarAvanzado.IsEnabled = False
            _pendingProposalId = ""
            Try
                _pendingProposalAmount = monto
                _modoAvanzado = True
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", monto)
                req.Add("basis", basis)
                req.Add("contract_type", tipo)
                req.Add("currency", currency)
                req.Add("duration", duracion)
                req.Add("duration_unit", GetDurUnit())
                req.Add("underlying_symbol", symbol)
                ' Agregar barrera si aplica
                If pnlBarrera.Visibility = System.Windows.Visibility.Visible Then
                    Dim barVal = txtBarrera.Text.Trim()
                    If Not String.IsNullOrEmpty(barVal) Then req.Add("barrier", barVal)
                    If txtBarrera2.Visibility = System.Windows.Visibility.Visible Then
                        Dim bar2Val = txtBarrera2.Text.Trim()
                        If Not String.IsNullOrEmpty(bar2Val) Then req.Add("barrier2", bar2Val)
                    End If
                End If
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch ex As Exception
                txtCotizacion.Text = "❌ Error: " & ex.Message
            End Try
        End Sub

        Private Async Sub btnEjecutarAvanzado_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If String.IsNullOrEmpty(_pendingProposalId) Then
                System.Windows.MessageBox.Show("Cotiza primero antes de ejecutar.") : Return
            End If
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then Return
            Try
                btnEjecutarAvanzado.IsEnabled = False
                Dim buyReq As New JObject()
                buyReq.Add("buy", _pendingProposalId)
                buyReq.Add("price", _pendingProposalAmount)
                Dim bytes = Encoding.UTF8.GetBytes(buyReq.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                txtTradingOpEstado.Text = "⏳ Ejecutando orden avanzada..."
                _pendingProposalId = ""
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error: " & ex.Message
            End Try
        End Sub

        Private Async Function EscucharTradingAsync() As Task
            Dim buffer(32767) As Byte
            Try
                Do While _tradingWs.State = WebSocketState.Open AndAlso Not _tradingCts.IsCancellationRequested
                    Dim sb As New System.Text.StringBuilder()
                    Dim result As WebSocketReceiveResult
                    Do
                        result = Await _tradingWs.ReceiveAsync(New ArraySegment(Of Byte)(buffer), _tradingCts.Token)
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                    Loop While Not result.EndOfMessage

                    If result.MessageType = WebSocketMessageType.Close Then Exit Do

                    Dim json As String = sb.ToString()
                    Dispatcher.Invoke(Sub() ProcesarMensajeTrading(json))
                Loop
            Catch
            End Try
        End Function

        Private Sub ProcesarMensajeTrading(json As String)
            Try
                Dim obj = JObject.Parse(json)
                Dim msgType = obj("msg_type")?.ToString()

                ' La nueva API mete msg_type dentro del objeto error — normalizar
                If String.IsNullOrEmpty(msgType) AndAlso obj("error") IsNot Nothing Then
                    msgType = obj("error")?("msg_type")?.ToString()
                End If

                ' Si hay error (con o sin msg_type), mostrarlo siempre
                If obj("error") IsNot Nothing Then
                    Dim errMsg = obj("error")?("message")?.ToString()
                    Dim errCode = obj("error")?("code")?.ToString()
                    Dim errDetails = obj("error")?("details")?.ToString()
                    Dim fullErr = "❌ " & errMsg & If(String.IsNullOrEmpty(errCode), "", " [" & errCode & "]") &
                                  If(String.IsNullOrEmpty(errDetails), "", " — " & errDetails)
                    txtTradingOpEstado.Text = fullErr
                    txtTradeResult.Text = fullErr
                    txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                    Return
                End If

                Select Case msgType
                    Case "balance"
                        Dim bal = obj("balance")
                        If bal IsNot Nothing Then
                            Dim balVal As Decimal = CDec(bal("balance"))
                            Dim cur As String = bal("currency")?.ToString()
                            txtTradingBalance.Text = String.Format("{0} {1:N2}", cur, balVal)
                        End If

                    Case "proposal"
                        Dim propObj = obj("proposal")
                        If propObj IsNot Nothing Then
                            Dim proposalId = propObj("id")?.ToString()
                            Dim askPrice As Double = If(propObj("ask_price") IsNot Nothing, CDbl(propObj("ask_price")), _pendingProposalAmount)
                            Dim payout As Double = If(propObj("payout") IsNot Nothing, CDbl(propObj("payout")), 0)
                            Dim longcode = propObj("longcode")?.ToString()

                            If _modoAvanzado Then
                                ' Modo avanzado: mostrar cotización y esperar confirmación del usuario
                                _pendingProposalId = proposalId
                                _pendingProposalAmount = askPrice
                                Dim cotMsg = String.Format("💰 Pago: ${0:F2}  →  Ganancia potencial: ${1:F2}", askPrice, payout)
                                txtCotizacion.Text = cotMsg
                                txtTradingOpEstado.Text = If(longcode, longcode, cotMsg)
                                btnEjecutarAvanzado.IsEnabled = True
                            Else
                                ' Modo simple: ejecutar buy automáticamente
                                txtTradingOpEstado.Text = String.Format("💱 Cotización: ${0:F2} — Ejecutando...", askPrice)
                                txtTradeResult.Text = txtTradingOpEstado.Text
                                txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)
                                Dim buyReq As New JObject()
                                buyReq.Add("buy", proposalId)
                                buyReq.Add("price", askPrice)
                                Dim bytes = Encoding.UTF8.GetBytes(buyReq.ToString())
                                _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                            End If
                        End If

                    Case "buy"
                        Dim buyObj = obj("buy")
                        If buyObj IsNot Nothing Then
                            Dim op As New OperacionModel()
                            op.ContractId = buyObj("contract_id")?.ToString()
                            ' Extraer CALL/PUT del shortcode  ej: "CALL_R_100_17.4_..."
                            Dim shortcode = If(buyObj("shortcode")?.ToString(), "")
                            op.Tipo = If(shortcode.StartsWith("CALL"), "CALL", If(shortcode.StartsWith("PUT"), "PUT", "?"))
                            ' Simbolo limpio desde shortcode: segundo segmento
                            Dim parts = shortcode.Split("_"c)
                            op.Simbolo = If(parts.Length >= 3, parts(1) & "_" & parts(2), shortcode)
                            op.Monto = If(buyObj("buy_price") IsNot Nothing, CDec(buyObj("buy_price")), 0)
                            op.Estado = "Abierto"
                            op.ProfitLoss = 0
                            op.TiempoRestante = "..."
                            op.Hora = DateTime.Now.ToString("HH:mm:ss")
                            op.Duracion = ""
                            If _tradingClienteActual IsNot Nothing Then op.ClientId = _tradingClienteActual.Id
                            _tradingOperaciones.Insert(0, op)
                            Dim msgBuy = String.Format("✅ {0} abierto: {1} — ${2}", op.Tipo, op.ContractId, op.Monto)
                            txtTradingOpEstado.Text = msgBuy
                            txtTradeResult.Text = msgBuy
                            txtTradeResult.Foreground = New SolidColorBrush(Colors.LightGreen)
                            GuardarOperacionBD(op)
                            ' Suscribir actualizaciones en vivo del contrato
                            SuscribirContratoAsync(op.ContractId)
                        End If

                    Case "proposal_open_contract"
                        Dim poc = obj("proposal_open_contract")
                        If poc IsNot Nothing Then
                            Dim contractId = poc("contract_id")?.ToString()
                            Dim status = poc("status")?.ToString()
                            Dim profit As Decimal = If(poc("profit") IsNot Nothing, CDec(poc("profit")), 0)
                            Dim op = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractId)
                            If op IsNot Nothing Then
                                op.Estado = If(status = "won", "Ganado", If(status = "lost", "Perdido", "Abierto"))
                                op.ProfitLoss = profit
                                ' Entry spot
                                If poc("entry_tick") IsNot Nothing Then op.EntrySpot = poc("entry_tick").ToString()
                                ' Current spot
                                If poc("current_spot") IsNot Nothing Then op.SpotActual = poc("current_spot").ToString()
                                ' Tiempo restante (tick contracts)
                                Dim tickCount = If(poc("tick_count") IsNot Nothing, CInt(poc("tick_count")), 0)
                                Dim ticksPassed = If(poc("tick_passed") IsNot Nothing, CInt(poc("tick_passed")), 0)
                                If tickCount > 0 Then
                                    Dim ticksLeft = tickCount - ticksPassed
                                    op.TiempoRestante = If(ticksLeft > 0, ticksLeft & "t", "0t")
                                ElseIf poc("date_expiry") IsNot Nothing Then
                                    Dim expUnix = CLng(poc("date_expiry"))
                                    Dim expDt = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expUnix)
                                    Dim secsLeft = CInt((expDt - DateTime.UtcNow).TotalSeconds)
                                    op.TiempoRestante = If(secsLeft > 0, secsLeft & "s", "0s")
                                End If
                            End If
                        End If

                    Case "sell"
                        Dim sellObj = obj("sell")
                        If sellObj IsNot Nothing Then
                            Dim contractId = sellObj("contract_id")?.ToString()
                            Dim soldFor = If(sellObj("sold_for") IsNot Nothing, CDec(sellObj("sold_for")), 0D)
                            Dim op = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractId)
                            If op IsNot Nothing Then
                                op.Estado = "Cerrado"
                                op.ProfitLoss = soldFor - op.Monto
                                op.TiempoRestante = "—"
                            End If
                            txtTradingOpEstado.Text = String.Format("✅ Contrato cerrado. Recibido: {0:F2}", soldFor)
                        End If

                End Select
            Catch
            End Try
        End Sub

        Private Sub SuscribirContratoAsync(contractId As String)
#Disable Warning BC42358
            Task.Run(Async Function()
                Try
                    If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then Return
                    Dim req As New JObject()
                    req.Add("proposal_open_contract", 1)
                    req.Add("contract_id", contractId)
                    req.Add("subscribe", 1)
                    Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                    Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                Catch
                End Try
            End Function)
#Enable Warning BC42358
        End Sub

        Private Async Sub BtnCerrarOperacion_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim btn = TryCast(sender, System.Windows.Controls.Button)
            If btn Is Nothing Then Return
            Dim contractId = btn.Tag?.ToString()
            If String.IsNullOrEmpty(contractId) Then Return
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.")
                Return
            End If
            Try
                Dim req As New JObject()
                req.Add("sell", contractId)
                req.Add("price", 0)   ' 0 = vender a precio de mercado
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                txtTradingOpEstado.Text = "⏳ Cerrando contrato " & contractId & "..."
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error al cerrar: " & ex.Message
            End Try
        End Sub

        Private Sub GuardarOperacionBD(op As OperacionModel)
            Task.Run(Async Function()
                Try
                    Dim repo As New ClientRepository()
                    Await repo.GuardarOperacionAsync(op)
                Catch
                End Try
            End Function)
        End Sub
    End Class
End Namespace
