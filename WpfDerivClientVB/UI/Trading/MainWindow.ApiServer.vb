' ============================================================
' MainWindow.ApiServer.vb
' Contenido: Tab "Server" completo:
'   - Control del servidor HTTP embebido (TradingApiServer)
'   - Log de peticiones recibidas
'   - Análisis de mercado: EMA(5), EMA(10), RSI(14) +
'     últimas 3 velas → recomendación CALL / PUT / NEUTRAL
'   - El servidor HTTP opera a traves de DerivTradingService
'     (GetStatus, GetOperations, ExecuteTradeAsync, ExecuteSellAsync)
' ============================================================
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ===================================================
        '  CONTROL DEL SERVIDOR API
        ' ===================================================
        ' Dispatcher unico de velas: enruta cada vela cerrada a TODOS los motores live
        ' activos. Cada motor filtra internamente si la vela aplica a su symbol/timeframe.
        Private _candleDispatcher As Action(Of CandleModel, String, String) = Nothing

        Private Sub btnApiServer_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _apiServer IsNot Nothing AndAlso _apiServer.IsRunning Then
                ' Desconectar el dispatcher de vela cerrada antes de detener
                If _candleDispatcher IsNot Nothing Then
                    RemoveHandler Me.OnCandleCerrada, _candleDispatcher
                    _candleDispatcher = Nothing
                End If
                _apiServer.Stop()
                _apiServer = Nothing
                btnApiServer.Content = "▶ Iniciar Servidor"
                btnApiServer.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
                txtApiUrl.Text = ""
                AgregarLogApi("[Sistema] Servidor API detenido.")
            Else
                Dim port As Integer = 8765
                Integer.TryParse(txtApiPort.Text.Trim(), port)
                _apiServer = New TradingApiServer(port)
                ' El servidor HTTP opera a traves del servicio de trading (sin depender de la ventana)
                _apiServer.GetStatus       = AddressOf Trading.GetStatus
                _apiServer.GetOperations   = AddressOf Trading.GetOperations
                _apiServer.ExecuteTrade    = AddressOf Trading.ExecuteTradeAsync
                _apiServer.ExecuteSell     = AddressOf Trading.ExecuteSellAsync
                _apiServer.LogAction       = AddressOf AgregarLogApi
                _apiServer.DescargaService = _descargaService
                _apiServer.OnTradeLiveAbierto = AddressOf TaggearOpLiveEnGrid
                ' Dispatcher de velas: notifica a todos los motores live activos.
                ' CRITICAL: cada motor procesa en Task.Run (thread pool) para NO bloquear
                ' el UI thread. Con velas de 1m y multi-estrategia, procesar en UI thread
                ' congela toda la app.
                _candleDispatcher = Sub(c As CandleModel, sym As String, tf As String)
                                        If _apiServer Is Nothing Then Return
                                        Dim motoresSnapshot As List(Of LiveTestMotor)
                                        SyncLock _apiServer.LiveMotors
                                            motoresSnapshot = _apiServer.LiveMotors.Values.ToList()
                                        End SyncLock
                                        For Each motor In motoresSnapshot
                                            Dim m = motor   ' capture para el closure
                                            Task.Run(Sub()
                                                         Try
                                                             m.ProcesarCandleCerradaConContexto(c, sym, tf)
                                                         Catch ex As Exception
                                                             AppLogger.Warn("[CandleDispatcher] " & ex.Message)
                                                         End Try
                                                     End Sub)
                                        Next
                                    End Sub
                AddHandler Me.OnCandleCerrada, _candleDispatcher
                ' Inicializar el registry de estrategias dinamicas
                EstrategiasDinamicasRegistry.Inicializar()
                Try
                    _apiServer.Start()
                    btnApiServer.Content = "⏹ Detener Servidor"
                    btnApiServer.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
                    txtApiUrl.Text = String.Format("http://localhost:{0}/api/help", port)
                    AgregarLogApi(String.Format("[Sistema] Servidor iniciado en http://localhost:{0}/", port))
                    ' Auto-reanudar sesiones live que quedaron activas en BD (post-crash).
                    ' La estrategia se carga del registry, el motor se recrea, se abre WS background.
                    Dim _ignore = Dispatcher.BeginInvoke(
                        Sub() Tareas.EnSegundoPlano(ReanudarSesionesLiveActivasAsync(), "Reanudar sesiones live"),
                        System.Windows.Threading.DispatcherPriority.Background)
                Catch ex As Exception
                    System.Windows.MessageBox.Show("No se pudo iniciar el servidor: " & ex.Message)
                    _apiServer = Nothing
                End Try
            End If
        End Sub

        Private Sub AgregarLogApi(mensaje As String)
            Dispatcher.Invoke(Sub()
                                  Dim lineas As String() = txtApiLog.Text.Split(New String() {Environment.NewLine}, StringSplitOptions.None)
                                  Dim lista As New System.Collections.Generic.List(Of String)(lineas)
                                  lista.Add(mensaje)
                                  Do While lista.Count > 200
                                      lista.RemoveAt(0)
                                  Loop
                                  txtApiLog.Text = String.Join(Environment.NewLine, lista)
                                  txtApiLog.ScrollToEnd()
                              End Sub)
        End Sub

        ''' <summary>
        ''' Marca una operacion del grid de Trading Live como originada por una estrategia.
        ''' Se invoca desde LiveTestMotor cuando confirma contract_id en Deriv.
        ''' La OperacionModel ya fue creada por ProcesarMensajeTrading "buy" — solo agregamos metadatos.
        ''' </summary>
        Private Sub TaggearOpLiveEnGrid(contractId As String, estrategia As String, sesionId As Integer, opId As Integer)
            Trading.TaggearOperacion(contractId, estrategia, sesionId, opId)
        End Sub

        Private Sub btnLimpiarLog_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            txtApiLog.Text = ""
        End Sub

        ' ===================================================
        '  ANALISIS DE MERCADO
        ' ===================================================
        Private Async Sub btnAnalizar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await AnalizarMercadoAsync(False)
        End Sub

        Private Async Sub btnAnalizarYOperar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await AnalizarMercadoAsync(True)
        End Sub

        Private Async Function AnalizarMercadoAsync(autoOperar As Boolean) As Task
            Dim symbolItem = TryCast(cmbAnalisisSymbol.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim symbol As String = If(symbolItem IsNot Nothing AndAlso symbolItem.Tag IsNot Nothing, symbolItem.Tag.ToString(), "R_100")

            Dim periodoItem = TryCast(cmbAnalisisPeriodo.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim periodMinutes As Integer = 20
            If periodoItem IsNot Nothing AndAlso periodoItem.Tag IsNot Nothing Then
                Integer.TryParse(periodoItem.Tag.ToString(), periodMinutes)
            End If

            btnAnalizar.IsEnabled = False
            btnAnalizarYOperar.IsEnabled = False
            txtAnalisisResult.Text = String.Format("⏳ Obteniendo {0} velas de {1}...", periodMinutes, symbol)
            txtAnalisisResult.Foreground = New SolidColorBrush(Colors.Yellow)

            Try
                ' Por la conexion autenticada (la API publica antigua ya no existe)
                Dim responseObj As JObject = Await DerivDatos.PedirVelasAsync(symbol, periodMinutes, 60, 8000)
                If responseObj("ok") Is Nothing OrElse Not CBool(responseObj("ok")) Then
                    txtAnalisisResult.Text = "Error: " & If(responseObj("error")?.ToString(), "sin respuesta de Deriv.")
                    txtAnalisisResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                    Return
                End If

                If responseObj("candles") Is Nothing Then
                    Dim errMsg As String = responseObj("error")?("message")?.ToString()
                    txtAnalisisResult.Text = "❌ Error: " & If(errMsg, "Respuesta inesperada.")
                    txtAnalisisResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                    Return
                End If

                Dim candles As JArray = CType(responseObj("candles"), JArray)
                If candles.Count < 15 Then
                    txtAnalisisResult.Text = String.Format("❌ Insuficientes datos: {0} velas (se necesitan al menos 15).", candles.Count)
                    txtAnalisisResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                    Return
                End If

                ' Convertir JArray → List(Of CandleModel) para la estrategia
                Dim candleList As New List(Of CandleModel)()
                For Each tok As JToken In candles
                    candleList.Add(New CandleModel With {
                        .Epoch      = CLng(tok("epoch")),
                        .OpenPrice  = CDec(tok("open")),
                        .High       = CDec(tok("high")),
                        .Low        = CDec(tok("low")),
                        .ClosePrice = CDec(tok("close"))
                    })
                Next

                ' Delegar al registro de estrategias — EstrategiaEmaRsi
                Dim res = New EstrategiaEmaRsi().Analizar(candleList)
                Dim recomendacion As String = res.Senal
                Dim confianza     As Double = res.Confianza
                Dim ema5          As Decimal = If(res.Datos.ContainsKey("EMA5"),  res.Datos("EMA5"),  0D)
                Dim ema10         As Decimal = If(res.Datos.ContainsKey("EMA10"), res.Datos("EMA10"), 0D)
                Dim rsi14         As Decimal = If(res.Datos.ContainsKey("RSI14"), res.Datos("RSI14"), 50D)

                Dim sb2 As New System.Text.StringBuilder()
                sb2.AppendLine(String.Format("Símbolo : {0}  |  Período: {1} velas x 1min", symbol, candles.Count))
                sb2.AppendLine(String.Format("EMA(5)  : {0:F5}", ema5))
                sb2.AppendLine(String.Format("EMA(10) : {0:F5}", ema10))
                sb2.AppendLine(String.Format("RSI(14) : {0:F2}{1}", rsi14, If(rsi14 < 30, "  ← SOBREVENTA", If(rsi14 > 70, "  ← SOBRECOMPRA", ""))))
                sb2.AppendLine(String.Format("Motivo  : {0}", res.Motivo))
                sb2.AppendLine(String.Format("Confianza: {0:F0}%", confianza))
                sb2.AppendLine()
                If recomendacion = "NEUTRAL" Then
                    sb2.AppendLine("⚖ NEUTRAL — Sin señal clara. No operar.")
                ElseIf recomendacion = "CALL" Then
                    sb2.AppendLine(String.Format("📈 RECOMENDACIÓN: CALL  ({0:F0}% confianza)", confianza))
                Else
                    sb2.AppendLine(String.Format("📉 RECOMENDACIÓN: PUT   ({0:F0}% confianza)", confianza))
                End If

                txtAnalisisResult.Text = sb2.ToString().TrimEnd()

                If recomendacion = "CALL" Then
                    txtAnalisisResult.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
                ElseIf recomendacion = "PUT" Then
                    txtAnalisisResult.Foreground = New SolidColorBrush(Color.FromRgb(&HF3, &H8B, &HA8))
                Else
                    txtAnalisisResult.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HAD, &HC8))
                End If

                ' Auto-operar si se solicitó y hay conexión trading activa
                If autoOperar AndAlso recomendacion <> "NEUTRAL" Then
                    If Trading.EstaConectado Then
                        Dim monto As Double = 1.0
                        Dim duracion As Integer = 5
                        Double.TryParse(txtTradingMonto.Text.Trim(), monto)
                        Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion)
                        If monto <= 0 Then monto = 1.0
                        If duracion <= 0 Then duracion = 5

                        ' Pasa por DerivTradingService.ExecuteTradeAsync: sujeto al gestor de riesgo
                        ' (antes enviaba la propuesta directo al WebSocket, sin limites).
                        Dim autoBody As New JObject From {
                            {"contract_type", recomendacion},
                            {"symbol", symbol},
                            {"amount", monto},
                            {"basis", "stake"},
                            {"currency", "USD"},
                            {"duration", duracion},
                            {"duration_unit", "t"}
                        }
                        Dim autoRes = Await Trading.ExecuteTradeAsync(autoBody)
                        Dim autoOk = autoRes("ok") IsNot Nothing AndAlso CBool(autoRes("ok"))
                        txtAnalisisResult.Text = txtAnalisisResult.Text & Environment.NewLine &
                            If(autoOk,
                               String.Format("🤖 Operación {0} ejecutada automáticamente.", recomendacion),
                               "⛔ Operación automática no ejecutada: " & If(autoRes("error")?.ToString(), "error desconocido"))
                    Else
                        txtAnalisisResult.Text = txtAnalisisResult.Text & Environment.NewLine & "⚠ Trading no conectado. No se ejecutó operación."
                    End If
                End If

            Catch ex As Exception
                txtAnalisisResult.Text = "❌ Error al analizar: " & ex.Message
                txtAnalisisResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
            Finally
                Dim isConnected = Trading.EstaConectado
                btnAnalizar.IsEnabled = isConnected
                btnAnalizarYOperar.IsEnabled = isConnected
            End Try
        End Function

    End Class
End Namespace
