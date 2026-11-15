' ============================================================
' MainWindow.DinLive.vb
' Partial Class del MainWindow — Estrategias Dinamicas: live test (streaming real o replay desde la BD) y refresco de metricas.
' (separado de MainWindow.EstrategiasDinamicas.vb sin cambios de codigo)
' ============================================================
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ── Live test — despacho segun fuente ───────────────────────────
        Private Async Function IniciarLiveAsync() As Task
            If lstEstrategias.SelectedItem Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona una estrategia de la lista.", "Live Test")
                Return
            End If
            If ObtenerFuente() = "replay" Then
                Await IniciarReplayBDAsync()
            Else
                ' Pre-flight: validar conexion trading + sync streaming + confirmar
                If Not Await ValidarYPrepararLiveAsync() Then Return
                Await IniciarLiveStreamingAsync()
            End If
        End Function

        ''' <summary>
        ''' Valida prerrequisitos antes de arrancar live trading real:
        '''   - Trading WS conectado (cuenta demo/real)
        '''   - API server corriendo
        '''   - Monto > 0
        ''' Sincroniza el streaming superior al symbol/timeframe de la estrategia.
        ''' Pide confirmacion al usuario mostrando los parametros.
        ''' Devuelve True si todo esta listo y el usuario confirmo.
        ''' </summary>
        Private Async Function ValidarYPrepararLiveAsync() As Task(Of Boolean)
            ' 1. API server
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then
                System.Windows.MessageBox.Show(
                    "El servidor API no esta activo. Arrancalo en la pestana 'Server'.",
                    "Live trading", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                Return False
            End If

            ' 2. Trading WS conectado
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show(
                    "La cuenta de trading no esta conectada." & Environment.NewLine & Environment.NewLine &
                    "Ve a la pestana 'Trading Live', selecciona el cliente, elige Demo y pulsa 'Conectar'.",
                    "Live trading", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                Return False
            End If

            ' 3. Parametros validos
            Dim nombre = lstEstrategias.SelectedItem.ToString()
            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()
            Dim monto As Decimal = 0D : Decimal.TryParse(txtDinMonto.Text, monto)
            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)

            If String.IsNullOrWhiteSpace(symbol) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Symbol y monto > 0 son requeridos.", "Live trading")
                Return False
            End If

            ' 4. Confirmacion
            Dim cuentaTxt = If(Trading.EsDemo, "DEMO (virtual)", "REAL")
            Dim mensaje = String.Format(
                "Iniciar trading EN VIVO?{0}{0}" &
                "Cuenta:      {1}{0}" &
                "Estrategia:  {2}{0}" &
                "Activo:      {3} / {4}{0}" &
                "Monto:       ${5:F2} por operacion{0}" &
                "Payout:      {6}% | Confianza min: {7}%{0}" &
                "Duracion:    {8} velas{0}{0}" &
                "Las operaciones se ejecutaran automaticamente en Deriv cada vez que cierre una vela y haya senal valida.",
                Environment.NewLine, cuentaTxt, nombre, symbol, timeframe, monto, payout, confianza, duracion)

            Dim confirm = System.Windows.MessageBox.Show(
                mensaje, "Confirmar live trading",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question)
            If confirm <> System.Windows.MessageBoxResult.Yes Then Return False

            ' 5. Asegurar flujo de velas para (symbol, timeframe).
            '    Si NO hay otro motor activo y el chart visible no esta en este symbol/tf,
            '    aprovechamos para mostrar el chart sincronizado (mejor UX para 1 estrategia).
            '    Si YA hay motores activos en otros activos, NO tocamos el foreground:
            '    abrimos un background WS en el subscriber y dejamos el chart como esta.
            Dim hayOtrosMotores As Boolean = (_apiServer IsNot Nothing AndAlso _apiServer.LiveMotors.Count > 0)
            If Not hayOtrosMotores Then
                txtDinEstado.Text = String.Format("Sincronizando streaming a {0}/{1}...", symbol, timeframe)
                Dim okSync = Await SincronizarStreamingAsync(symbol, timeframe)
                If Not okSync Then
                    System.Windows.MessageBox.Show(
                        String.Format("No se pudo sincronizar el streaming a {0}/{1}." & Environment.NewLine &
                                      "Verifica el simbolo o usa una temporalidad soportada (1m,5m,15m,1h,4h,1d).",
                                      symbol, timeframe),
                        "Live trading", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                    Return False
                End If
            Else
                ' Multi-estrategia: cada activo tiene su propia subscription background.
                Dim gran As Integer = If(TIMEFRAME_MAP.ContainsKey(timeframe), TIMEFRAME_MAP(timeframe), 60)
                txtDinEstado.Text = String.Format("Abriendo subscription background para {0}/{1}...", symbol, timeframe)
                Dim okBg As Boolean = Await _multiSub.SubscribeAsync(symbol, timeframe, gran)
                If Not okBg Then
                    System.Windows.MessageBox.Show(
                        String.Format("No se pudo abrir el WebSocket background para {0}/{1}.", symbol, timeframe),
                        "Live trading", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                    Return False
                End If
                ActualizarLogBD(String.Format("Subscription background activa: {0}/{1} (chart sigue en {2}/{3}).",
                                              symbol, timeframe, _currentSymbol, _currentTimeframe),
                                Colors.CornflowerBlue)
            End If

            Return True
        End Function

        ' ── Live test — Replay BD ────────────────────────────────────────
        Private Async Function IniciarReplayBDAsync() As Task
            Dim nombre = lstEstrategias.SelectedItem.ToString()
            Dim estrategia = EstrategiasDinamicasRegistry.GetByNombre(nombre)
            If estrategia Is Nothing Then
                txtDinEstado.Text = "Estrategia no encontrada: " & nombre
                Return
            End If

            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()

            If String.IsNullOrWhiteSpace(symbol) Then
                System.Windows.MessageBox.Show("Selecciona un activo de la lista.", "Replay BD")
                Return
            End If

            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)
            Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)

            Dim desde As Long = If(dpDinDesde.SelectedDate.HasValue,
                                   FechaAEpoch(dpDinDesde.SelectedDate.Value),
                                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30L * 86400)
            Dim hasta As Long = If(dpDinHasta.SelectedDate.HasValue,
                                   FechaAEpoch(dpDinHasta.SelectedDate.Value.AddDays(1)),
                                   DateTimeOffset.UtcNow.ToUnixTimeSeconds())

            Dim config As New LiveTestConfig() With {
                .Symbol = symbol,
                .Timeframe = timeframe,
                .DuracionVelas = duracion,
                .PayoutPct = payout,
                .MinConfianza = confianza,
                .MontoOperacion = monto
            }

            _dinReplayMotor = New LiveTestMotor()
            ' Sin ExecuteTrade → simulacion pura, sin trades reales en Deriv
            btnDinLiveIniciar.IsEnabled = False
            btnDinLiveDetener.IsEnabled = True
            txtDinEstado.Text = "Iniciando Replay BD..."
            txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
            txtDinWinRate.Text = "" : txtDinPnl.Text = "" : txtDinOps.Text = "" : txtDinDrawdown.Text = ""
            txtDinVersion.Text = String.Format("{0} v{1}", nombre, estrategia.Definicion.Version)
            txtDinBalance.Text = ""
            dgDinOperaciones.ItemsSource = Nothing

            Try
                ' tipoSesion="backtest" — replay NO contamina las queries de Trading Live (que filtran por 'live').
                Dim sesionId = Await _dinReplayMotor.IniciarAsync(config, estrategia, tipoSesion:="backtest")
                _dinUltimoSesionId = sesionId
                _dinLiveActivo = True
                txtDinSesionId.Text = "ID:" & sesionId

                ' Tarjeta en el panel: replay BD tambien aparece como estrategia "activa"
                ' mientras procesa; al terminar queda con metricas finales.
                AgregarTarjetaPanelReplay(sesionId, nombre, symbol, timeframe)

                ' Leer velas de BD
                ActualizarLogBD(String.Format("Consultando velas {0}/{1}...", symbol, timeframe), Colors.Gray)
                Dim dbConfig = ConfigManager.LoadDbConfig()
                Dim repoCandles As New CandleRepository(dbConfig.GetConnectionString())
                Dim candles = Await repoCandles.GetCandlesRangoAsync("Deriv", symbol, timeframe, desde, hasta)

                If candles.Count = 0 Then
                    ActualizarLogBD(String.Format("Sin velas en BD para {0}/{1} en el rango seleccionado. " &
                                                  "Usa 'Descarga Historica' para importar datos.", symbol, timeframe), Colors.Orange)
                    txtDinEstado.Text = "Sin datos en BD para ese rango."
                    Await _dinReplayMotor.DetenerAsync()
                    _dinReplayMotor = Nothing
                    _dinLiveActivo = False
                    Return
                End If

                ActualizarLogBD(String.Format("BD OK | {0}/{1}: {2:N0} velas cargadas para replay.",
                                              symbol, timeframe, candles.Count), Colors.LightGreen)
                txtDinEstado.Text = String.Format("Replay BD — {0} velas | {1} {2} | Sesion #{3}",
                                                  candles.Count, nombre, symbol, sesionId)

                ' CRITICO: el procesamiento del replay corre TODO en thread pool.
                ' Cada N velas postea una actualizacion de UI via Dispatcher.BeginInvoke
                ' a Background priority para no bloquear input/render.
                ' La UI queda libre para drag/click/scroll.
                Dim motorRef = _dinReplayMotor
                Dim totalCandles = candles.Count
                Dim procesadas As Integer = 0
                Await Task.Run(Sub()
                                   For Each candle In candles
                                       If Not _dinLiveActivo Then Exit For
                                       Try
                                           motorRef.ProcesarCandleCerrada(candle)
                                       Catch ex As Exception
                                           AppLogger.Warn("[Replay] " & ex.Message)
                                       End Try
                                       procesadas += 1
                                       If procesadas Mod 100 = 0 Then
                                           Dim p = procesadas
                                           Dispatcher.BeginInvoke(
                                               Sub() ActualizarMetricasReplay(motorRef, p, totalCandles),
                                               System.Windows.Threading.DispatcherPriority.Background)
                                       End If
                                   Next
                               End Sub)

                Await _dinReplayMotor.DetenerAsync()
                ActualizarMetricasReplay(_dinReplayMotor, procesadas, candles.Count)

                ' Marcar tarjeta del panel como "Detenida" con las metricas finales
                If _panelLive IsNot Nothing Then
                    Dim estadoFinal = _dinReplayMotor.GetEstado()
                    Dim ganF = CInt(estadoFinal("n_ganadas"))
                    Dim perF = CInt(estadoFinal("n_perdidas"))
                    Dim pnlF = CDec(estadoFinal("pnl_total"))
                    Dim mF As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, mF)
                    Dim pyF As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, pyF)
                    _panelLive.ActualizarMetricas("replay-" & sesionId,
                                                  0, ganF, perF,
                                                  ganF * mF * (pyF / 100D), -perF * mF,
                                                  pnlF, estado:="Detenida")
                End If

                If _dinLiveActivo Then
                    txtDinEstado.Text = String.Format("Replay completo — {0} velas | Sesion #{1}", procesadas, sesionId)
                    txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
                Else
                    txtDinEstado.Text = String.Format("Replay detenido — {0}/{1} velas", procesadas, candles.Count)
                    txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HAD, &HC8))
                End If

                If sesionId > 0 AndAlso _apiServer IsNot Nothing AndAlso _apiServer.IsRunning Then
                    Await CargarOperacionesSesionAsync(sesionId)
                End If

            Catch ex As Exception
                txtDinEstado.Text = "Error en replay: " & ex.Message
                txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HF3, &H8B, &HA8))
                ActualizarLogBD("Error: " & ex.Message, Colors.Salmon)
            Finally
                _dinLiveActivo = False
                _dinReplayMotor = Nothing
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
            End Try
        End Function

        Private Sub ActualizarMetricasReplay(motor As LiveTestMotor, procesadas As Integer, total As Integer)
            Try
                Dim estado = motor.GetEstado()
                Dim ganadas = CInt(estado("n_ganadas"))
                Dim perdidas = CInt(estado("n_perdidas"))
                Dim totalOps = CInt(estado("total_ops"))
                Dim pnl = CDec(estado("pnl_total"))
                Dim pend = CInt(estado("pendientes"))
                txtDinOps.Text = String.Format("{0} ops ({1}G/{2}P) +{3}pend  [{4}/{5}v]",
                                               totalOps, ganadas, perdidas, pend, procesadas, total)
                MostrarPnl(pnl)
                If ganadas + perdidas > 0 Then MostrarWinRate(CDec(ganadas) / CDec(ganadas + perdidas) * 100D)
                ' Refrescar tarjeta del replay en el panel lateral
                If _panelLive IsNot Nothing AndAlso _dinUltimoSesionId > 0 Then
                    ' Estimar montos de ganadas/perdidas con el monto fijo del config
                    Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)
                    Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
                    Dim ganMonto = ganadas * monto * (payout / 100D)
                    Dim perMonto = -perdidas * monto
                    Dim velAna As Integer = If(estado("velas_analizadas") IsNot Nothing,
                                               CInt(estado("velas_analizadas")), procesadas)
                    _panelLive.ActualizarMetricas("replay-" & _dinUltimoSesionId,
                                                  pend, ganadas, perdidas,
                                                  ganMonto, perMonto, pnl,
                                                  estado:="Replay",
                                                  velasAnalizadas:=velAna)
                End If
            Catch
            End Try
        End Sub

        ''' <summary>Agrega tarjeta al panel para una sesion de Replay BD (Estado="Replay").</summary>
        Private Sub AgregarTarjetaPanelReplay(sesionId As Integer, nombre As String, symbol As String, timeframe As String)
            If _panelLive Is Nothing Then Return
            _panelLive.UpsertCard(New EstrategiaActivaCard With {
                .Id = "replay-" & sesionId,
                .Nombre = nombre,
                .Symbol = symbol,
                .Timeframe = timeframe,
                .HoraInicio = DateTime.Now,
                .Abiertas = 0, .Ganadas = 0, .Perdidas = 0,
                .GanadasMontoUsd = 0D, .PerdidasMontoUsd = 0D,
                .Pnl = 0D, .Estado = "Replay"
            })
        End Sub

        ' ── Live test — Streaming ────────────────────────────────────────
        Private Async Function IniciarLiveStreamingAsync() As Task
            Dim nombre = lstEstrategias.SelectedItem.ToString()
            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()
            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)
            Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)

            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then
                txtDinEstado.Text = "El servidor API no esta activo. Arrancalo en la pestana Server."
                Return
            End If

            Try
                ' ventana=30: el motor empieza a evaluar tras 30 velas (no 100 default).
                ' Suficiente para indicadores tipicos (RSI14, BB20, EMA21).
                Dim body = New JObject From {
                    {"nombre", nombre}, {"symbol", symbol}, {"timeframe", timeframe},
                    {"duracion_velas", duracion}, {"payout", payout},
                    {"min_confianza", confianza}, {"monto", monto},
                    {"ventana", 30}
                }
                Using client As HttpClient = NuevoClienteApiLocal()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim content As New StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    Dim httpResp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/iniciar", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await httpResp.Content.ReadAsStringAsync())

                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        _dinLiveActivo = True
                        _dinReplayMotor = Nothing
                        _dinUltimoSesionId = CInt(json("sesion_id"))
                        Dim cuentaTag = If(Trading.EsDemo, "DEMO", "REAL")
                        txtDinEstado.Text = String.Format("LIVE {0} — Sesion #{1} | {2} | {3}/{4} | Esperando vela cerrada...",
                                                          cuentaTag, _dinUltimoSesionId, nombre, symbol, timeframe)
                        txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HF5, &HC2, &HE7))
                        ActualizarLogBD(String.Format("LIVE {0} activo — {1}/{2} | Trades automaticos al cerrar cada vela con senal valida.",
                                                       cuentaTag, symbol, timeframe),
                                        Colors.CornflowerBlue)
                        btnDinLiveIniciar.IsEnabled = True   ' Permitir agregar mas estrategias en paralelo
                        btnDinLiveDetener.IsEnabled = True
                        txtDinSesionId.Text = "ID:" & _dinUltimoSesionId
                        IniciarTimerRefreshLive()

                        ' Agregar tarjeta al panel lateral con los parametros de la estrategia
                        AgregarTarjetaPanelLive(_dinUltimoSesionId, nombre, symbol, timeframe)
                    Else
                        txtDinEstado.Text = "Error iniciando live: " & json("error")?.ToString()
                    End If
                End Using
            Catch ex As Exception
                txtDinEstado.Text = "Error: " & ex.Message
            End Try
        End Function

        ' ── Detener ──────────────────────────────────────────────────────
        Private Async Function DetenerLiveAsync() As Task
            ' Replay BD: basta bajar la bandera
            If _dinReplayMotor IsNot Nothing Then
                _dinLiveActivo = False
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
                Return
            End If
            ' Streaming via API server
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Using client As HttpClient = NuevoClienteApiLocal()
                    Dim content As New StringContent("{}", Encoding.UTF8, "application/json")
                    Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/detener", _apiServer.Port), content)
                End Using
                _dinLiveActivo = False
                DetenerTimerRefreshLive()
                txtDinEstado.Text = "Live streaming detenido (todas las estrategias)."
                txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HAD, &HC8))
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
                ' Limpiar tarjetas live del panel (las demo se conservan)
                If _panelLive IsNot Nothing Then
                    LimpiarTarjetasLiveDelPanel()
                End If
                ' Cerrar TODOS los WebSockets background ya que no queda ningun motor
                If _multiSub IsNot Nothing Then
                    Await _multiSub.UnsubscribeAllAsync()
                End If
                If _dinUltimoSesionId > 0 Then Await CargarOperacionesSesionAsync(_dinUltimoSesionId)
            Catch ex As Exception
                txtDinEstado.Text = "Error deteniendo: " & ex.Message
            End Try
        End Function

        ' Contador de ticks para hacer las tareas pesadas (recargar BD) menos seguido
        Private _refreshTickCounter As Integer = 0

        ' ── Timer de refresco (Streaming) ────────────────────────────────
        Private Sub IniciarTimerRefreshLive()
            If _dinRefreshTimer IsNot Nothing Then
                _dinRefreshTimer.Stop()
                RemoveHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
            End If
            ' DispatcherPriority.Normal para que el timer NO sea starved por
            ' Dispatcher.BeginInvoke a Send priority (que era el bug: timer en Background = starvation).
            _dinRefreshTimer = New System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal)
            ' 2s para que el contador de velas y metricas del panel se vean reactivos.
            ' La carga pesada (HTTP a BD) se hace solo cada N ticks (ver OnRefreshLiveTick).
            _dinRefreshTimer.Interval = TimeSpan.FromSeconds(2)
            _refreshTickCounter = 0
            AddHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
            _dinRefreshTimer.Start()
        End Sub

        Private Sub DetenerTimerRefreshLive()
            If _dinRefreshTimer IsNot Nothing Then
                _dinRefreshTimer.Stop()
                RemoveHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
                _dinRefreshTimer = Nothing
            End If
        End Sub

        Private Async Sub OnRefreshLiveTick(sender As Object, e As EventArgs)
            If Not _dinLiveActivo OrElse _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            _refreshTickCounter += 1

            ' ── Bloque rapido (cada 2s): metricas en memoria de los motores ──────
            Try
                ' Snapshot bajo lock para evitar InvalidOperationException si el
                ' API server modifica el diccionario en otro hilo.
                Dim snapshot As List(Of KeyValuePair(Of Integer, LiveTestMotor))
                SyncLock _apiServer.LiveMotors
                    snapshot = _apiServer.LiveMotors.ToList()
                End SyncLock

                ' Metricas globales de la barra superior (primer motor activo)
                If snapshot.Count > 0 Then
                    Dim estado = snapshot(0).Value.GetEstado()
                    Dim ganadas = CInt(estado("n_ganadas"))
                    Dim perdidas = CInt(estado("n_perdidas"))
                    Dim totalOps = CInt(estado("total_ops"))
                    Dim pnl = CDec(estado("pnl_total"))
                    Dim pend = CInt(estado("pendientes"))
                    txtDinOps.Text = String.Format("{0} ops ({1}G/{2}P) +{3}pend", totalOps, ganadas, perdidas, pend)
                    MostrarPnl(pnl)
                    If ganadas + perdidas > 0 Then MostrarWinRate(CDec(ganadas) / CDec(ganadas + perdidas) * 100D)
                End If

                ' Tarjetas del panel lateral
                If _panelLive IsNot Nothing Then
                    Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)
                    Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
                    For Each kv In snapshot
                        Dim sid = kv.Key
                        Dim e2 = kv.Value.GetEstado()
                        Dim ga = CInt(e2("n_ganadas"))
                        Dim pe = CInt(e2("n_perdidas"))
                        Dim pn = CDec(e2("pnl_total"))
                        Dim ab = CInt(e2("pendientes"))
                        Dim va = If(e2("velas_analizadas") IsNot Nothing, CInt(e2("velas_analizadas")), 0)
                        Dim estaPausado As Boolean = (e2("pausado") IsNot Nothing AndAlso CBool(e2("pausado")))
                        Dim estadoCard = If(estaPausado, "Pausada", "Activa")
                        ' Config avanzada
                        Dim mMonto = If(e2("monto_operacion") IsNot Nothing, CDec(e2("monto_operacion")), 0D)
                        Dim mConfMin = If(e2("min_confianza") IsNot Nothing, CInt(e2("min_confianza")), 0)
                        Dim mDur = If(e2("duracion_velas") IsNot Nothing, CInt(e2("duracion_velas")), 0)
                        Dim mPayout = If(e2("payout_pct") IsNot Nothing, CDec(e2("payout_pct")), 0D)
                        Dim mVentana = If(e2("ventana_indicadores") IsNot Nothing, CInt(e2("ventana_indicadores")), 0)
                        Dim mSenales = If(e2("total_senales") IsNot Nothing, CInt(e2("total_senales")), 0)
                        Dim mHorasTrans = If(e2("horas_transcurridas") IsNot Nothing, CDec(e2("horas_transcurridas")), 0D)
                        _panelLive.ActualizarMetricas("live-" & sid,
                                                      ab, ga, pe,
                                                      ga * monto * (payout / 100D), -pe * monto,
                                                      pn, estado:=estadoCard,
                                                      velasAnalizadas:=va)
                        ' Solo actualizar campos NO editables (no pisar lo que el usuario teclea)
                        _panelLive.ActualizarSoloLectura("live-" & sid, mSenales, mPayout, mVentana, mHorasTrans)
                    Next
                End If
            Catch ex As Exception
                AppLogger.Warn("[OnRefreshLiveTick.fast] " & ex.Message)
            End Try

            ' ── Bloque pesado (cada ~30s = 15 ticks): recargar operaciones + refrescar PnL periodos ─
            If _refreshTickCounter Mod 15 = 0 Then
                Try
                    If _dinUltimoSesionId > 0 Then Await CargarOperacionesSesionAsync(_dinUltimoSesionId)
                Catch ex As Exception
                    AppLogger.Warn("[OnRefreshLiveTick.slow] " & ex.Message)
                End Try
                Try
                    Await PanelLiveRefrescarPnlPeriodosAsync()
                Catch ex As Exception
                    AppLogger.Warn("[OnRefreshLiveTick.pnl] " & ex.Message)
                End Try
            End If
        End Sub

    End Class

End Namespace
