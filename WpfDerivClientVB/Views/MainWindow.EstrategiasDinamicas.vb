' ============================================================
' MainWindow.EstrategiasDinamicas.vb
' Partial Class del MainWindow — Tab "Estrategias Dinamicas"
'
' Fuente de velas:
'   - Replay BD  : lee velas de PostgreSQL candle a candle
'                  (sin trades reales, usa el rango Desde/Hasta)
'   - Streaming  : recibe velas del WebSocket activo via
'                  el evento OnCandleCerrada (trades reales demo)
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

        ' ── Estado del tab ──────────────────────────────────────────────
        Private _dinUltimoSesionId As Integer = 0
        Private _dinLiveActivo As Boolean = False
        Private _dinRefreshTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinReplayMotor As LiveTestMotor = Nothing
        Private _dinPollTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinUltimoSesionIdBD As Integer = 0   ' ultimo ID conocido en BD para detectar nuevas sesiones
        ' Guard: evita que eventos de XAML init disparen logica antes de que los controles existan
        Private _dinTabListo As Boolean = False
        ' Lista completa de sesiones cargadas (sin filtro), para poder filtrar por activo en memoria
        Private _dinSesionesCompletas As List(Of SesionRow) = New List(Of SesionRow)()
        ' Panel lateral de estrategias activas (instanciado en runtime)
        Private _panelLive As EstrategiasLivePanelControl = Nothing

        ' ── Inicializacion ──────────────────────────────────────────────
        Private Sub InicializarTabDinamicas()
            dpDinDesde.SelectedDate = DateTime.Today.AddDays(-30)
            dpDinHasta.SelectedDate = DateTime.Today
            EstrategiasDinamicasRegistry.Inicializar()
            CargarListaEstrategias()
            _dinTabListo = True
            Dispatcher.BeginInvoke(New Action(Async Sub()
                                                  Await CargarActivosBDAsync()
                                                  Await CargarSesionesAsync()
                                              End Sub), System.Windows.Threading.DispatcherPriority.Loaded)
            ' ── Polling: detecta sesiones nuevas creadas via API/MCP cada 5s ──
            _dinPollTimer = New System.Windows.Threading.DispatcherTimer()
            _dinPollTimer.Interval = TimeSpan.FromSeconds(5)
            AddHandler _dinPollTimer.Tick, AddressOf OnPollNuevaSesionTick
            _dinPollTimer.Start()

            ' ── Panel lateral de estrategias activas: solo wiring, sin dummies ───
            ' Las tarjetas se agregan cuando el usuario inicia una estrategia (live o replay).
            CablearPanelLive()
        End Sub

        ''' <summary>
        ''' Crea la instancia del panel lateral de estrategias activas, la inyecta
        ''' en el ContentControl del XAML y conecta sus eventos con handlers locales.
        ''' Patron usado en AdminDashboard.vb (SidebarContainer.Content = New AdminSidebar()).
        ''' </summary>
        Private Sub CablearPanelLive()
            If panelEstrategiasLiveContainer Is Nothing Then Return
            _panelLive = New EstrategiasLivePanelControl()
            panelEstrategiasLiveContainer.Content = _panelLive

            AddHandler _panelLive.VerDetalleSolicitado, Sub(id)
                                                            ActualizarLogBD("Ver detalle estrategia: " & id, Colors.LightSkyBlue)
                                                        End Sub
            AddHandler _panelLive.PausarReanudarSolicitado, AddressOf OnPanelPausarReanudarSolicitado
            AddHandler _panelLive.ConfigGuardarSolicitada, AddressOf OnPanelConfigGuardarSolicitada
            AddHandler _panelLive.DetenerSolicitado, AddressOf OnPanelDetenerSolicitado
        End Sub

        ''' <summary>
        ''' Handler del boton "Detener" en cada tarjeta. Las tarjetas creadas por
        ''' "Iniciar Live" tienen Id = "live-{sesionId}" para poder mapearlas al motor.
        ''' </summary>
        Private Async Sub OnPanelDetenerSolicitado(id As String)
            If String.IsNullOrEmpty(id) Then Return
            ' Tarjetas de replay (terminadas) o cualquier otra que no sea live: solo quitar
            If Not id.StartsWith("live-") Then
                _panelLive.RemoverCard(id)
                ActualizarLogBD("Tarjeta quitada: " & id, Colors.LightSkyBlue)
                Return
            End If
            Dim sesionStr = id.Substring("live-".Length)
            Dim sesionId As Integer = 0
            If Not Integer.TryParse(sesionStr, sesionId) Then Return

            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then
                ActualizarLogBD("API server no esta corriendo, no se puede detener.", Colors.Salmon)
                Return
            End If

            Try
                ' Capturar symbol/tf del motor antes de detenerlo (lo necesitamos para
                ' decidir si se cierra el background WS asociado).
                Dim symMotor As String = ""
                Dim tfMotor As String = ""
                If _apiServer.LiveMotors.ContainsKey(sesionId) Then
                    Dim estado = _apiServer.LiveMotors(sesionId).GetEstado()
                    symMotor = If(estado("symbol")?.ToString(), "")
                    tfMotor = If(estado("timeframe")?.ToString(), "")
                End If

                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(15)
                    Dim body = New JObject From {{"sesion_id", sesionId}}
                    Dim content As New StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    Dim resp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/detener", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await resp.Content.ReadAsStringAsync())
                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        _panelLive.RemoverCard(id)
                        ActualizarLogBD(String.Format("Sesion #{0} detenida.", sesionId), Colors.Salmon)
                        ' Si nadie mas usa este symbol/tf, cerrar el WS background asociado
                        Await CerrarBackgroundSiNoSeUsa(symMotor, tfMotor)
                    Else
                        ActualizarLogBD("Error deteniendo: " & json("mensaje")?.ToString(), Colors.Salmon)
                    End If
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error detener live: " & ex.Message, Colors.Salmon)
            End Try
        End Sub

        ''' <summary>
        ''' Si no queda ningun motor activo usando (symbol, tf), cierra el WS background.
        ''' </summary>
        Private Async Function CerrarBackgroundSiNoSeUsa(symbol As String, timeframe As String) As Task
            If _multiSub Is Nothing Then Return
            If String.IsNullOrEmpty(symbol) OrElse String.IsNullOrEmpty(timeframe) Then Return
            If _apiServer Is Nothing Then Return
            Dim sigueEnUso As Boolean = False
            For Each m In _apiServer.LiveMotors.Values
                Dim e = m.GetEstado()
                If String.Equals(e("symbol")?.ToString(), symbol, StringComparison.OrdinalIgnoreCase) _
                   AndAlso String.Equals(e("timeframe")?.ToString(), timeframe, StringComparison.OrdinalIgnoreCase) Then
                    sigueEnUso = True
                    Exit For
                End If
            Next
            If Not sigueEnUso Then
                Await _multiSub.UnsubscribeAsync(symbol, timeframe)
            End If
        End Function

        ''' <summary>Quita del panel todas las tarjetas live (las demo se conservan).</summary>
        Private Sub LimpiarTarjetasLiveDelPanel()
            _panelLive.RemoverCardsConPrefijo("live-")
        End Sub

        ''' <summary>
        ''' Click "Aplicar" en el Expander de una card. Recibe la card editada con
        ''' Monto/MinConfianza/DuracionVelas/HorasOperacion/LimiteGanancia/LimitePerdida
        ''' actualizados y los aplica al motor en runtime.
        ''' </summary>
        Private Sub OnPanelConfigGuardarSolicitada(id As String, card As EstrategiaActivaCard)
            If _apiServer Is Nothing OrElse card Is Nothing Then Return
            If String.IsNullOrEmpty(id) OrElse Not id.StartsWith("live-") Then Return
            Dim sidStr = id.Substring("live-".Length)
            Dim sid As Integer = 0
            If Not Integer.TryParse(sidStr, sid) Then Return
            Dim motor As LiveTestMotor = Nothing
            SyncLock _apiServer.LiveMotors
                _apiServer.LiveMotors.TryGetValue(sid, motor)
            End SyncLock
            If motor Is Nothing Then
                ActualizarLogBD(String.Format("Motor de sesion #{0} no esta cargado.", sid), Colors.Salmon)
                Return
            End If
            Try
                motor.ActualizarConfig(card.Monto, card.MinConfianza, card.DuracionVelas,
                                       card.HorasOperacion, card.LimiteGanancia, card.LimitePerdida)
                ActualizarLogBD(String.Format(
                    "Sesion #{0} config aplicada: monto=${1:F2} conf={2}% dur={3} horas={4} limG=${5:F2} limP=${6:F2}",
                    sid, card.Monto, card.MinConfianza, card.DuracionVelas,
                    card.HorasOperacion, card.LimiteGanancia, card.LimitePerdida),
                    Colors.LightGreen)
            Catch ex As Exception
                ActualizarLogBD("Error aplicando config: " & ex.Message, Colors.Salmon)
            End Try
        End Sub

        ''' <summary>
        ''' Toggle Pausa/Reanuda. Encuentra el motor por sesion_id (parseado desde el
        ''' Id de la tarjeta "live-N") y llama Pausar()/Reanudar(). El estado de la card
        ''' se actualiza para que el icono cambie (▶ vs ⏸).
        ''' </summary>
        Private Sub OnPanelPausarReanudarSolicitado(id As String)
            If String.IsNullOrEmpty(id) OrElse Not id.StartsWith("live-") Then
                ActualizarLogBD("Pausa/reanuda solo aplica a sesiones live: " & id, Colors.Khaki)
                Return
            End If
            If _apiServer Is Nothing Then Return
            Dim sidStr = id.Substring("live-".Length)
            Dim sid As Integer = 0
            If Not Integer.TryParse(sidStr, sid) Then Return
            Dim motor As LiveTestMotor = Nothing
            SyncLock _apiServer.LiveMotors
                _apiServer.LiveMotors.TryGetValue(sid, motor)
            End SyncLock
            If motor Is Nothing Then
                ActualizarLogBD(String.Format("Motor de sesion #{0} no esta en memoria.", sid), Colors.Salmon)
                Return
            End If
            If motor.EstaPausado Then
                motor.Reanudar()
                ' Refrescar tarjeta — el siguiente OnRefreshLiveTick la marcara como Activa
                _panelLive.ActualizarMetricas("live-" & sid, 0, 0, 0, 0D, 0D, 0D, estado:="Activa")
                ActualizarLogBD(String.Format("Sesion #{0} REANUDADA.", sid), Colors.LightGreen)
            Else
                motor.Pausar()
                _panelLive.ActualizarMetricas("live-" & sid, 0, 0, 0, 0D, 0D, 0D, estado:="Pausada")
                ActualizarLogBD(String.Format("Sesion #{0} PAUSADA. Las ops abiertas seguiran resolviendose.", sid),
                                Colors.Khaki)
            End If
        End Sub

        ''' <summary>
        ''' Auto-reanuda las sesiones live que quedaron marcadas como "activa" en BD
        ''' (porque la app crasheo o se cerro sin detenerlas). Para cada una:
        '''   - Recrea el LiveTestMotor con la misma estrategia y config
        '''   - Lo registra en _apiServer.LiveMotors (para que reciba candles)
        '''   - Abre subscription background del symbol/timeframe
        '''   - Agrega tarjeta al panel lateral
        ''' Las operaciones huerfanas se reconcilian aparte via RecuperarOperacionesLivePendientesAsync.
        ''' </summary>
        Friend Async Function ReanudarSesionesLiveActivasAsync() As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Dim repo As New LiveTestRepository()
            Dim activas As List(Of BacktestSesion)
            Try
                activas = Await repo.GetSesionesLiveActivasAsync()
            Catch ex As Exception
                ActualizarLogBD("Error consultando sesiones activas: " & ex.Message, Colors.Salmon)
                Return
            End Try
            If activas Is Nothing OrElse activas.Count = 0 Then
                ActualizarLogBD("Sin sesiones live activas que reanudar.", Colors.LightGreen)
                Return
            End If

            Dim reanudadas As Integer = 0
            For Each ses In activas
                Try
                    ' Resolver la estrategia desde el registry (debe estar en disco)
                    Dim defEst = EstrategiasDinamicasRegistry.GetByNombre(ses.Estrategia)
                    If defEst Is Nothing Then
                        ActualizarLogBD(String.Format(
                            "Sesion #{0} usa estrategia '{1}' que ya no existe. Marcandola como cerrada.",
                            ses.Id, ses.Estrategia), Colors.Khaki)
                        Try
                            Await repo.CerrarSesionAsync(ses.Id, ses.TotalOperaciones,
                                                         ses.TotalSenales, ses.WinRate, ses.ProfitLossTotal)
                        Catch
                        End Try
                        Continue For
                    End If

                    ' Recrear el motor con los mismos params
                    Dim cfg As New LiveTestConfig With {
                        .Symbol = ses.Symbol,
                        .Timeframe = ses.Timeframe,
                        .DuracionVelas = ses.DuracionVelas,
                        .PayoutPct = ses.PayoutPct,
                        .MinConfianza = ses.MinConfianza,
                        .MontoOperacion = 1D,
                        .VentanaIndicadores = 30
                    }
                    Dim motor As New LiveTestMotor()
                    motor.ExecuteTrade = _apiServer.ExecuteTrade
                    motor.OnTradeLiveAbierto = _apiServer.OnTradeLiveAbierto

                    ' IniciarAsync crea una sesion nueva. En modo reanudacion, queremos
                    ' RE-USAR la sesion existente. Hacemos InjectarSesion y arrancamos manualmente.
                    motor.InyectarSesionExistente(ses.Id, cfg, defEst)

                    ' Restaurar metricas historicas y ops abiertas que vinieron del crash.
                    Try
                        Dim datos = Await repo.GetMetricasYAbiertasSesionAsync(ses.Id)
                        motor.RestaurarMetricasDesde(datos.Ganadas, datos.Perdidas, datos.PnlTotal)
                        For Each op In datos.Abiertas
                            motor.AgregarPendienteExterna(op.OperacionId, op.ContractId, op.Tipo,
                                                          op.EpochEntrada, op.PrecioEntrada, op.Monto)
                        Next
                        If datos.Abiertas.Count > 0 OrElse datos.Ganadas > 0 OrElse datos.Perdidas > 0 Then
                            ActualizarLogBD(String.Format(
                                "Sesion #{0}: {1}G/{2}P/{3}abiertas restauradas (PnL={4:+0.00;-0.00;0.00}).",
                                ses.Id, datos.Ganadas, datos.Perdidas, datos.Abiertas.Count, datos.PnlTotal),
                                Colors.LightGreen)
                        End If
                    Catch ex As Exception
                        AppLogger.Warn("[ReanudarSesion] No se pudieron restaurar metricas: " & ex.Message)
                    End Try

                    SyncLock _apiServer.LiveMotors
                        _apiServer.LiveMotors(ses.Id) = motor
                    End SyncLock

                    ' Background subscription para que llegue el flujo de candles
                    If _multiSub IsNot Nothing AndAlso TIMEFRAME_MAP.ContainsKey(ses.Timeframe) Then
                        Await _multiSub.SubscribeAsync(ses.Symbol, ses.Timeframe, TIMEFRAME_MAP(ses.Timeframe))
                    End If

                    ' Agregar tarjeta al panel
                    AgregarTarjetaPanelLive(ses.Id, ses.Estrategia, ses.Symbol, ses.Timeframe)

                    reanudadas += 1
                    ActualizarLogBD(String.Format("Sesion #{0} reanudada: {1} | {2}/{3}",
                                                  ses.Id, ses.Estrategia, ses.Symbol, ses.Timeframe),
                                    Colors.LightGreen)
                Catch ex As Exception
                    ActualizarLogBD(String.Format("No se pudo reanudar sesion #{0}: {1}",
                                                  ses.Id, ex.Message), Colors.Salmon)
                End Try
            Next

            If reanudadas > 0 Then
                _dinLiveActivo = True
                IniciarTimerRefreshLive()
                ActualizarLogBD(String.Format("Auto-reanudacion completa: {0} sesion(es) live activa(s).",
                                              reanudadas), Colors.LightGreen)
            End If
        End Function

        ''' <summary>
        ''' Agrega una tarjeta al panel lateral cuando arranca una estrategia live.
        ''' La tarjeta usa Id = "live-{sesionId}" para vincularla al motor en el server.
        ''' </summary>
        Private Sub AgregarTarjetaPanelLive(sesionId As Integer, nombre As String, symbol As String, timeframe As String)
            If _panelLive Is Nothing Then Return
            ' Leer config de los controles del form
            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)
            Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)
            _panelLive.UpsertCard(New EstrategiaActivaCard With {
                .Id = "live-" & sesionId,
                .Nombre = nombre,
                .Symbol = symbol,
                .Timeframe = timeframe,
                .HoraInicio = DateTime.Now,
                .Abiertas = 0, .Ganadas = 0, .Perdidas = 0,
                .GanadasMontoUsd = 0D, .PerdidasMontoUsd = 0D,
                .Pnl = 0D, .Estado = "Activa",
                .Monto = monto, .MinConfianza = confianza, .DuracionVelas = duracion,
                .PayoutPct = payout, .VentanaIndicadores = 30,
                .HorasOperacion = 0, .LimiteGanancia = 0D, .LimitePerdida = 0D
            })
        End Sub

        ' ── Helpers que el resto de la app puede llamar para reflejar estado de trading ─
        ''' <summary>Refresca el indicador de conexion del panel lateral.</summary>
        Friend Sub PanelLiveSetConexion(conectado As Boolean)
            If _panelLive Is Nothing Then Return
            _panelLive.ActualizarConexion(conectado)
        End Sub

        ''' <summary>Refresca el saldo total del panel lateral.</summary>
        Friend Sub PanelLiveSetSaldo(saldo As Decimal, Optional currency As String = "USD")
            If _panelLive Is Nothing Then Return
            _panelLive.ActualizarSaldoTotal(saldo, currency)
        End Sub

        ''' <summary>
        ''' Consulta la BD y actualiza HOY/SEMANA/MES en el panel lateral.
        ''' Llamado desde OnRefreshLiveTick cada 2s y al conectar trading.
        ''' </summary>
        Friend Async Function PanelLiveRefrescarPnlPeriodosAsync() As Task
            If _panelLive Is Nothing Then Return
            Try
                Dim repo As New LiveTestRepository()
                Dim r = Await repo.GetPnlPeriodosAsync()
                _panelLive.ActualizarPnlPeriodos(r.Hoy, r.Semana, r.Mes)
            Catch
            End Try
        End Function

        ''' <summary>Pone el saldo en "--" (cuando se desconecta).</summary>
        Friend Sub PanelLiveLimpiarSaldo()
            If _panelLive Is Nothing Then Return
            _panelLive.LimpiarSaldo()
        End Sub

        ''' <summary>Mockup de tarjetas para previsualizar el panel mientras se cablea la logica real.</summary>
        Private Sub CargarDemoEstrategiasActivas()
            If _panelLive Is Nothing Then Return
            ' Saldo y conexion los maneja MainWindow.Trading.vb cuando el usuario conecta.
            ' Mientras tanto, mostrar 0 desconectado.
            _panelLive.ActualizarSaldo(0D, 0D, 0D, 0D, conectado:=False)
            _panelLive.LimpiarSaldo()

            _panelLive.UpsertCard(New EstrategiaActivaCard With {
                .Id = "demo-1",
                .Nombre = "BollingerRsiReversal_d20",
                .Symbol = "R_100", .Timeframe = "1h",
                .HoraInicio = DateTime.Now.AddHours(-4).AddMinutes(-35),
                .Abiertas = 2, .Ganadas = 5, .Perdidas = 2,
                .GanadasMontoUsd = 18.5D, .PerdidasMontoUsd = -5D,
                .Pnl = 28.28D, .Estado = "Activa"
            })
            _panelLive.UpsertCard(New EstrategiaActivaCard With {
                .Id = "demo-2",
                .Nombre = "ADXReversal_v1",
                .Symbol = "R_50", .Timeframe = "5m",
                .HoraInicio = DateTime.Now.AddHours(-3).AddMinutes(-5),
                .Abiertas = 0, .Ganadas = 0, .Perdidas = 0,
                .GanadasMontoUsd = 0D, .PerdidasMontoUsd = 0D,
                .Pnl = 0D, .Estado = "Analizando"
            })
            _panelLive.UpsertCard(New EstrategiaActivaCard With {
                .Id = "demo-3",
                .Nombre = "BollingerMomentum_v1",
                .Symbol = "frxUSOIL", .Timeframe = "15m",
                .HoraInicio = DateTime.Now.AddHours(-14).AddMinutes(-20),
                .Abiertas = 0, .Ganadas = 12, .Perdidas = 8,
                .GanadasMontoUsd = 24.8D, .PerdidasMontoUsd = -29D,
                .Pnl = -4.2D, .Estado = "Pausada"
            })
        End Sub

        ' ── Poll: detectar nueva sesion creada externamente (MCP / API) ─────
        Private Async Sub OnPollNuevaSesionTick(sender As Object, e As EventArgs)
            ' Solo actuar cuando NO hay un backtest/live en curso para no pisar resultados
            If _dinLiveActivo OrElse Not btnDinBacktest.IsEnabled Then Return
            Try
                Dim repo As New BacktestRepository()
                Dim sesiones = Await Task.Run(Async Function()
                                                  Return Await repo.GetSesionesAsync(1)
                                              End Function)
                If sesiones.Count = 0 Then Return
                Dim ultimaId = sesiones(0).Id
                ' Primera vez: solo guardar referencia sin cargar
                If _dinUltimoSesionIdBD = 0 Then
                    _dinUltimoSesionIdBD = ultimaId
                    Return
                End If
                ' Hay sesion nueva
                If ultimaId > _dinUltimoSesionIdBD Then
                    _dinUltimoSesionIdBD = ultimaId
                    _dinUltimoSesionId = ultimaId
                    txtDinSesionId.Text = String.Format("#{0}", ultimaId)
                    Dim s = sesiones(0)
                    txtDinEstado.Text = String.Format(
                        "Nueva sesion via API — #{0} | {1} | {2}/{3} | {4} ops",
                        ultimaId, s.Estrategia, s.Symbol, s.Timeframe, s.TotalOperaciones)
                    MostrarWinRate(s.WinRate * 100D)
                    MostrarPnl(s.ProfitLossTotal)
                    txtDinOps.Text = String.Format("{0} ops", s.TotalOperaciones)
                    txtDinDrawdown.Text = String.Format("DD:{0:F2}%", s.MaxDrawdown)
                    ' Cargar version desde registry
                    Dim estReg = EstrategiasDinamicasRegistry.GetByNombre(s.Estrategia)
                    If estReg IsNot Nothing Then
                        txtDinVersion.Text = String.Format("{0} v{1}", s.Estrategia, estReg.Definicion.Version)
                    Else
                        txtDinVersion.Text = s.Estrategia
                    End If
                    txtDinBalance.Text = ""
                    ' Refrescar lista de sesiones y cargar operaciones de la nueva
                    Dim nombreSel = If(lstEstrategias.SelectedItem?.ToString(), "")
                    Dim filtro = If(chkDinTodasEstrategias?.IsChecked = True, "", nombreSel)
                    Await CargarSesionesAsync(filtro)
                    Await CargarOperacionesSesionAsync(ultimaId)
                    ActualizarLogBD(String.Format(
                        "Sesion #{0} cargada automaticamente | {1} | Win:{2:F1}% | PnL:{3:F2}",
                        ultimaId, s.Estrategia, s.WinRate * 100D, s.ProfitLossTotal),
                        If(s.WinRate >= 0.5, Colors.LightGreen, Colors.Orange))
                End If
            Catch
                ' Ignorar errores de poll silenciosamente
            End Try
        End Sub

        Private Sub CargarListaEstrategias()
            lstEstrategias.Items.Clear()
            Try
                Dim estrategias = EstrategiasDinamicasRegistry.GetAll()
                For Each est In estrategias
                    lstEstrategias.Items.Add(est.Nombre)
                Next
                If estrategias.Count > 0 Then
                    lstEstrategias.SelectedIndex = 0
                End If
            Catch ex As Exception
                ActualizarLogBD("Error cargando estrategias: " & ex.Message, Colors.Salmon)
            End Try
        End Sub

        ' ── Cambio de fuente ─────────────────────────────────────────────
        Private Async Sub cmbDinFuente_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            ' Guard: este evento puede dispararse durante InitializeComponent() cuando
            ' IsSelected="True" activa la seleccion inicial, antes de que los controles
            ' del tab esten creados. Ignoramos hasta que InicializarTabDinamicas termine.
            If Not _dinTabListo Then Return
            Dim fuente = ObtenerFuente()
            If fuente = "replay" Then
                Await CargarActivosBDAsync()
            Else
                ' Modo Streaming: el activo se escribe manualmente
                ActualizarLogBD("Streaming: el activo se obtiene del WebSocket activo. Asegurate de iniciar el streaming.", Colors.CornflowerBlue)
            End If
        End Sub

        ' ── Cargar activos disponibles en BD ─────────────────────────────
        Private Async Function CargarActivosBDAsync() As Task
            ActualizarLogBD("Consultando activos en BD...", Colors.Gray)
            Try
                Dim cs = ConfigManager.LoadDbConfig().GetConnectionString()
                Dim resumen As New List(Of String)()
                Dim simbolos As New List(Of String)()

                Using conn As New NpgsqlConnection(cs)
                    Await conn.OpenAsync()
                    Dim sql = "SELECT symbol, timeframe, COUNT(*) AS total, " &
                              "MIN(open_time) AS desde, MAX(open_time) AS hasta " &
                              "FROM public.candles WHERE LOWER(platform) = 'deriv' " &
                              "GROUP BY symbol, timeframe ORDER BY symbol, timeframe"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            While Await reader.ReadAsync()
                                Dim sym = reader.GetString(0)
                                Dim tf = reader.GetString(1)
                                Dim total = reader.GetInt64(2)
                                Dim desde = reader.GetDateTime(3)
                                Dim hasta = reader.GetDateTime(4)
                                If Not simbolos.Contains(sym) Then simbolos.Add(sym)
                                resumen.Add(String.Format("{0}/{1}: {2:N0}v ({3:dd/MM/yy}-{4:dd/MM/yy})",
                                                          sym, tf, total, desde, hasta))
                            End While
                        End Using
                    End Using
                End Using

                ' Actualizar ComboBox de activos en el UI thread
                Dim prevSym = cmbDinSymbol.Text
                cmbDinSymbol.Items.Clear()
                For Each s In simbolos
                    cmbDinSymbol.Items.Add(s)
                Next
                ' Conservar seleccion previa o seleccionar el primero
                If simbolos.Contains(prevSym) Then
                    cmbDinSymbol.Text = prevSym
                ElseIf simbolos.Count > 0 Then
                    cmbDinSymbol.SelectedIndex = 0
                End If

                ' Actualizar ComboBox de filtro de sesiones con los mismos activos.
                ' Primer item vacio = "(todos)": muestra todas las sesiones sin filtrar.
                If cmbDinFiltroSimbolo IsNot Nothing Then
                    Dim prevFiltro = If(cmbDinFiltroSimbolo.SelectedItem?.ToString(), "")
                    cmbDinFiltroSimbolo.Items.Clear()
                    cmbDinFiltroSimbolo.Items.Add("")    ' = todos
                    For Each s In simbolos
                        cmbDinFiltroSimbolo.Items.Add(s)
                    Next
                    If simbolos.Contains(prevFiltro) Then
                        cmbDinFiltroSimbolo.SelectedItem = prevFiltro
                    Else
                        cmbDinFiltroSimbolo.SelectedIndex = 0   ' "" = todos
                    End If
                End If

                If resumen.Count = 0 Then
                    ActualizarLogBD("BD OK | Sin datos en la tabla candles (platform=deriv). Descarga historico primero.", Colors.Orange)
                Else
                    ActualizarLogBD("BD OK | " & String.Join("  |  ", resumen), Colors.LightGreen)
                End If

            Catch ex As Exception
                ActualizarLogBD("Error BD: " & ex.Message, Colors.Salmon)
            End Try
        End Function

        ' ── Helpers UI ──────────────────────────────────────────────────
        Private Sub ActualizarLogBD(texto As String, color As Color)
            ' Null-check defensivo: el control puede no estar listo si se llama
            ' muy temprano (ej. desde un evento disparado durante InitializeComponent).
            If txtDinLogBD Is Nothing Then Return
            txtDinLogBD.Text = texto
            txtDinLogBD.Foreground = New SolidColorBrush(color)
        End Sub

        Private Function ObtenerFuente() As String
            Dim item = TryCast(cmbDinFuente.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "replay")
        End Function

        Private Function ObtenerSymbol() As String
            Return If(cmbDinSymbol.Text, "").Trim()
        End Function

        Private Function ObtenerTimeframe() As String
            Dim item = TryCast(cmbDinTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "1h")
        End Function

        Private Shared Function FechaAEpoch(fecha As DateTime) As Long
            Return New DateTimeOffset(fecha.Year, fecha.Month, fecha.Day,
                                      0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        End Function

        ' ── Eventos de UI ───────────────────────────────────────────────
        Private Async Sub lstEstrategias_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            If Not _dinTabListo Then Return
            Dim nombre = If(lstEstrategias.SelectedItem?.ToString(), "")
            dgDinOperaciones.ItemsSource = Nothing
            ' Si "Ver todas las estrategias" esta marcado, no filtrar por estrategia
            Dim filtroEst = If(chkDinTodasEstrategias?.IsChecked = True, "", nombre)
            Await CargarSesionesAsync(filtroEst)
        End Sub

        ''' <summary>
        ''' Toggle del checkbox: recarga la lista de sesiones con o sin filtro de estrategia.
        ''' </summary>
        Private Async Sub chkDinTodasEstrategias_Changed(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not _dinTabListo Then Return
            Dim nombre = If(lstEstrategias.SelectedItem?.ToString(), "")
            Dim filtroEst = If(chkDinTodasEstrategias.IsChecked = True, "", nombre)
            Await CargarSesionesAsync(filtroEst)
        End Sub

        Private Async Sub btnRecargarEstrategias_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            EstrategiasDinamicasRegistry.Recargar()
            CargarListaEstrategias()
            If ObtenerFuente() = "replay" Then
                Await CargarActivosBDAsync()
            End If
        End Sub

        Private Async Sub btnDinBacktest_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarBacktestAsync()
        End Sub

        Private Async Sub btnDinLiveIniciar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await IniciarLiveAsync()
        End Sub

        Private Async Sub btnDinLiveDetener_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await DetenerLiveAsync()
        End Sub

        ' ── Backtest directo (no requiere API server) ────────────────────
        Private Async Function EjecutarBacktestAsync() As Task
            ' ── Validaciones ────────────────────────────────────────────────
            If lstEstrategias.SelectedItem Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona una estrategia de la lista.", "Backtest")
                Return
            End If
            Dim nombre = lstEstrategias.SelectedItem.ToString()

            Dim estrategia = EstrategiasDinamicasRegistry.GetByNombre(nombre)
            If estrategia Is Nothing Then
                System.Windows.MessageBox.Show("Estrategia no encontrada en el registry. Pulsa Recargar.", "Backtest")
                Return
            End If

            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()
            If String.IsNullOrWhiteSpace(symbol) Then
                System.Windows.MessageBox.Show("Selecciona o escribe un activo.", "Backtest")
                Return
            End If

            ' ── Leer parametros ─────────────────────────────────────────────
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

            ' ── UI: inicio ──────────────────────────────────────────────────
            btnDinBacktest.IsEnabled = False
            btnDinLiveIniciar.IsEnabled = False
            txtDinWinRate.Text = "" : txtDinPnl.Text = "" : txtDinOps.Text = ""
            txtDinDrawdown.Text = "" : txtDinSesionId.Text = ""
            txtDinVersion.Text = "" : txtDinBalance.Text = ""
            dgDinOperaciones.ItemsSource = Nothing

            Dim sw = System.Diagnostics.Stopwatch.StartNew()

            ' ── Timer de progreso (actualiza cada segundo) ──────────────────
            Dim progressTimer As New System.Windows.Threading.DispatcherTimer()
            progressTimer.Interval = TimeSpan.FromSeconds(1)
            AddHandler progressTimer.Tick, Sub()
                                               txtDinEstado.Text = String.Format(
                                                   "Ejecutando backtest {0} / {1} / {2}... {3:F0}s",
                                                   nombre, symbol, timeframe, sw.Elapsed.TotalSeconds)
                                           End Sub
            progressTimer.Start()

            Try
                ' ── Configurar y ejecutar backtest en hilo de pool ───────────
                ActualizarLogBD(String.Format("Ejecutando backtest: {0} | {1}/{2} | {3}-{4}",
                                               nombre, symbol, timeframe,
                                               DateTimeOffset.FromUnixTimeSeconds(desde).ToString("dd/MM/yy"),
                                               DateTimeOffset.FromUnixTimeSeconds(hasta).ToString("dd/MM/yy")),
                                Colors.CornflowerBlue)

                Dim configBt As New BacktestConfig With {
                    .Symbol = symbol,
                    .Timeframe = timeframe,
                    .DesdeEpoch = desde,
                    .HastaEpoch = hasta,
                    .DuracionVelas = duracion,
                    .PayoutPct = payout,
                    .MinConfianza = confianza,
                    .VentanaIndicadores = 100,
                    .Cuentas = New List(Of BacktestCuenta) From {
                        New BacktestCuenta With {
                            .NombreCuenta = "Simulacion",
                            .BalanceInicial = 10000D,
                            .BalanceActual = 10000D,
                            .MontoPorOperacion = monto
                        }
                    }
                }

                ' Ejecutar en hilo de pool para no bloquear UI
                Dim resultado As BacktestResultado = Await Task.Run(
                    Async Function() As Task(Of BacktestResultado)
                        Dim motor As New BacktestMotor()
                        Return Await motor.EjecutarAsync(configBt, estrategia)
                    End Function)

                ' ── BacktestMotor ya guarda en BD y asigna Sesion.Id ────────
                _dinUltimoSesionId = resultado.Sesion.Id

                ' ── Mostrar resultados ───────────────────────────────────────
                Dim s = resultado.Sesion
                Dim wr = s.WinRate * 100D
                Dim totalOps = s.TotalOperaciones
                Dim ganadas = resultado.OperacionesGanadas.Count
                Dim perdidas = resultado.OperacionesPerdidas.Count
                Dim version = estrategia.Definicion.Version
                Dim balInicial = If(resultado.CuentasFinales.Count > 0,
                                    resultado.CuentasFinales(0).BalanceInicial, 10000D)
                Dim balFinal = If(resultado.CuentasFinales.Count > 0,
                                    resultado.CuentasFinales(0).BalanceActual, balInicial)

                txtDinEstado.Text = String.Format(
                    "Completado en {0:F1}s — Sesion #{1} — {2} senales → {3} ops",
                    sw.Elapsed.TotalSeconds, _dinUltimoSesionId, s.TotalSenales, totalOps)

                MostrarWinRate(wr)
                MostrarPnl(s.ProfitLossTotal)
                txtDinOps.Text = String.Format("{0}G / {1}P", ganadas, perdidas)
                txtDinDrawdown.Text = String.Format("DD:{0:F2}%", s.MaxDrawdown)
                txtDinSesionId.Text = String.Format("#{0}", _dinUltimoSesionId)
                txtDinVersion.Text = String.Format("{0} v{1}", nombre, version)
                txtDinBalance.Text = String.Format("${0:N0} -> ${1:N2}", balInicial, balFinal)

                ActualizarLogBD(String.Format(
                    "Backtest OK | #{0} | {1}/{2} | Win:{3:F1}% | PnL:{4:F2} | {5} ops | {6:F1}s",
                    _dinUltimoSesionId, symbol, timeframe, wr,
                    s.ProfitLossTotal, totalOps, sw.Elapsed.TotalSeconds),
                    If(wr >= 50, Colors.LightGreen, Colors.Orange))

                ' ── Refrescar lista de sesiones ─────────────────────────────
                Await CargarSesionesAsync(nombre)

                ' ── Poblar tabla de operaciones ──────────────────────────────
                Dim filas = resultado.Operaciones.
                                OrderBy(Function(o) o.EpochEntrada).
                                Select(Function(o) New OperacionRow With {
                                    .Estrategia = nombre,
                                    .Version = "v" & version,
                                    .Tipo = o.Tipo,
                                    .Epoch = DateTimeOffset.FromUnixTimeSeconds(o.EpochEntrada).ToString("dd/MM/yyyy HH:mm"),
                                    .EpochSalida = If(o.EpochSalida > 0,
                                                      DateTimeOffset.FromUnixTimeSeconds(o.EpochSalida).ToString("dd/MM/yyyy HH:mm"),
                                                      ""),
                                    .Entrada = String.Format("{0:F5}", o.PrecioEntrada),
                                    .Salida = String.Format("{0:F5}", o.PrecioSalida),
                                    .Estado = o.Estado,
                                    .Pnl = String.Format("{0:+0.00;-0.00;0.00}", o.ProfitLoss),
                                    .Confianza = String.Format("{0}%", o.Confianza),
                                    .BalanceInicio = String.Format("${0:N2}", balInicial),
                                    .Balance = String.Format("${0:N2}", o.BalancePost),
                                    .Cuenta = o.CuentaNombre
                                }).ToList()
                dgDinOperaciones.ItemsSource = filas

            Catch ex As Exception
                txtDinEstado.Text = "Error: " & ex.Message
                ActualizarLogBD("Error backtest: " & ex.Message, Colors.Salmon)
                AppLogger.Warn("[EstrategiasDin] EjecutarBacktestAsync: " & ex.Message)
            Finally
                sw.Stop()
                progressTimer.Stop()
                btnDinBacktest.IsEnabled = True
                btnDinLiveIniciar.IsEnabled = True
            End Try
        End Function

        ' ── Helpers de metricas ──────────────────────────────────────────
        Private Sub MostrarWinRate(wr As Decimal)
            txtDinWinRate.Text = String.Format("Win: {0:F1}%", wr)
            txtDinWinRate.Foreground = New SolidColorBrush(If(wr >= 50, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
        End Sub

        Private Sub MostrarPnl(pnl As Decimal)
            txtDinPnl.Text = String.Format("PnL: {0:F2}", pnl)
            txtDinPnl.Foreground = New SolidColorBrush(If(pnl >= 0, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
        End Sub

        ' ── Cargar lista de sesiones (filtrada por estrategia) ───────────
        Private Async Function CargarSesionesAsync(Optional estrategia As String = "") As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                ' Limite alto cuando se carga "todas las estrategias" para no perder
                ' sesiones de activos antiguos en el filtro en memoria.
                Dim limite As Integer = If(String.IsNullOrWhiteSpace(estrategia), 1000, 200)
                Dim url = String.Format("http://localhost:{0}/api/dinamicas/sesiones?limite={1}", _apiServer.Port, limite)
                If Not String.IsNullOrWhiteSpace(estrategia) Then
                    url &= "&estrategia=" & Uri.EscapeDataString(estrategia)
                End If
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(15)
                    Dim resp = Await client.GetStringAsync(url)
                    Dim json = JObject.Parse(resp)
                    Dim arr = TryCast(json("sesiones"), JArray)
                    If arr Is Nothing Then Return

                    Dim lista As New List(Of SesionRow)()
                    Dim verde = Color.FromRgb(&HA6, &HE3, &HA1)
                    Dim rojo = Color.FromRgb(&HF3, &H8B, &HA8)
                    For Each item As JObject In arr
                        Dim wr = CDec(item("win_rate"))
                        Dim pnl = CDec(item("profit_loss"))
                        Dim pf = CDec(item("profit_factor"))
                        Dim ops = CLng(item("total_ops"))
                        Dim senales = CLng(item("total_senales"))
                        Dim sid = CInt(item("id"))
                        Dim est = If(item("estrategia")?.ToString(), "")
                        Dim sym = If(item("symbol")?.ToString(), "")
                        Dim tf = If(item("timeframe")?.ToString(), "")
                        Dim dur = CInt(If(item("duracion_velas"), 0))
                        Dim pay = CDec(If(item("payout_pct"), 85))
                        Dim conf = CInt(If(item("min_confianza"), 55))
                        Dim desde = If(item("desde_str")?.ToString(), "")
                        Dim hasta = If(item("hasta_str")?.ToString(), "")
                        Dim fecha = If(item("created_at")?.ToString(), "")
                        Dim fechaCorta As String = fecha
                        Try
                            Dim dt = DateTime.ParseExact(fecha, "yyyy-MM-dd HH:mm",
                                         System.Globalization.CultureInfo.InvariantCulture)
                            fechaCorta = dt.ToString("dd/MM HH:mm")
                        Catch
                        End Try
                        Dim pnlStr = String.Format("{0:+0.00;-0.00;0.00}", pnl)
                        lista.Add(New SesionRow With {
                            .Id = sid,
                            .IdCorto = String.Format("#{0}", sid),
                            .FechaHora = fechaCorta,
                            .WinRate = String.Format("{0:F1}%", wr),
                            .WinRateNum = wr,
                            .WinBrush = New SolidColorBrush(If(wr >= 54.05, verde, rojo)),
                            .Resumen = String.Format("{0} · {1}/{2} · {3}ops · {4}",
                                                          est, sym, tf, ops, pnlStr),
                            .Estrategia = est,
                            .Symbol = sym,
                            .Timeframe = tf,
                            .DuracionV = dur,
                            .PayoutPct = pay,
                            .MinConf = conf,
                            .DesdeStr = desde,
                            .HastaStr = hasta,
                            .TotalSenales = senales,
                            .TotalOps = ops,
                            .ProfitLoss = pnl,
                            .ProfitFactor = pf
                        })
                    Next
                    _dinSesionesCompletas = lista
                    AplicarFiltroSesiones()
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error cargando sesiones: " & ex.Message, Colors.Salmon)
            End Try
        End Function

        ''' <summary>
        ''' Aplica el filtro actual de simbolo a la lista completa y refresca el ListView.
        ''' Filtro vacio ("") = mostrar todas las sesiones.
        ''' </summary>
        Private Sub AplicarFiltroSesiones()
            If lstDinSesiones Is Nothing Then Return
            Dim filtroSym = If(cmbDinFiltroSimbolo?.SelectedItem?.ToString(), "")
            Dim soloGanadoras As Boolean = (chkDinSoloGanadoras IsNot Nothing AndAlso chkDinSoloGanadoras.IsChecked = True)

            Dim resultado As IEnumerable(Of SesionRow) = _dinSesionesCompletas

            ' Filtro por symbol
            If Not String.IsNullOrEmpty(filtroSym) Then
                resultado = resultado.Where(Function(s) s.Symbol = filtroSym)
            End If

            ' Filtro "solo ganadoras": WinRate > 50% y PnL > 0
            If soloGanadoras Then
                resultado = resultado.Where(Function(s) s.WinRateNum > 50D AndAlso s.ProfitLoss > 0D)
            End If

            lstDinSesiones.ItemsSource = resultado.ToList()
        End Sub

        Private Sub cmbDinFiltroSimbolo_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            If Not _dinTabListo Then Return
            AplicarFiltroSesiones()
        End Sub

        ''' <summary>Handler unificado para checkboxes de filtro de la lista.</summary>
        Private Sub chkDinFiltroChanged(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not _dinTabListo Then Return
            AplicarFiltroSesiones()
        End Sub

        ' ── Seleccion de sesion → barra config + operaciones ─────────────
        Private Async Sub lstDinSesiones_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            Dim row = TryCast(lstDinSesiones.SelectedItem, SesionRow)
            If row Is Nothing Then
                brdDinConfig.Visibility = System.Windows.Visibility.Collapsed
                btnDinEliminar.IsEnabled = False
                Return
            End If
            _dinUltimoSesionId = row.Id
            txtDinSesionId.Text = row.IdCorto

            ' Poblar barra de configuracion
            Dim estReg = EstrategiasDinamicasRegistry.GetByNombre(row.Estrategia)
            Dim version = If(estReg IsNot Nothing, " v" & estReg.Definicion.Version, "")
            txtDinCfgId.Text = row.IdCorto
            txtDinCfgEstrategia.Text = row.Estrategia & version
            txtDinCfgSymbol.Text = String.Format("{0} / {1}", row.Symbol, row.Timeframe)
            txtDinCfgDuracion.Text = String.Format("{0} velas", row.DuracionV)
            txtDinCfgPayout.Text = String.Format("{0}%", row.PayoutPct)
            txtDinCfgConfianza.Text = String.Format("{0}%", row.MinConf)
            txtDinCfgRango.Text = If(row.DesdeStr <> "" AndAlso row.HastaStr <> "",
                                          String.Format("{0} -> {1}", row.DesdeStr, row.HastaStr), "completo")
            txtDinCfgSenales.Text = String.Format("{0:N0}", row.TotalSenales)
            txtDinCfgOps.Text = String.Format("{0:N0}", row.TotalOps)
            txtDinCfgWin.Text = String.Format("{0:F1}%", row.WinRateNum)
            txtDinCfgWin.Foreground = New SolidColorBrush(
                If(row.WinRateNum >= 54.05, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
            txtDinCfgPnl.Text = String.Format("{0:+0.00;-0.00;0.00}", row.ProfitLoss)
            txtDinCfgPnl.Foreground = New SolidColorBrush(
                If(row.ProfitLoss >= 0, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
            txtDinCfgPf.Text = String.Format("{0:F3}", row.ProfitFactor)
            brdDinConfig.Visibility = System.Windows.Visibility.Visible
            btnDinEliminar.IsEnabled = True

            ' Poblar controles de entrada para re-ejecucion
            For i As Integer = 0 To lstEstrategias.Items.Count - 1
                If lstEstrategias.Items(i)?.ToString() = row.Estrategia Then
                    lstEstrategias.SelectedIndex = i
                    Exit For
                End If
            Next
            cmbDinFuente.SelectedIndex = 0
            cmbDinSymbol.Text = row.Symbol
            For i As Integer = 0 To cmbDinTimeframe.Items.Count - 1
                Dim ci = TryCast(cmbDinTimeframe.Items(i), System.Windows.Controls.ComboBoxItem)
                If ci IsNot Nothing AndAlso ci.Tag?.ToString() = row.Timeframe Then
                    cmbDinTimeframe.SelectedIndex = i
                    Exit For
                End If
            Next
            txtDinPayout.Text = row.PayoutPct.ToString()
            txtDinConfianza.Text = row.MinConf.ToString()
            txtDinDuracion.Text = row.DuracionV.ToString()

            ' Actualizar barra de metricas superior
            MostrarWinRate(row.WinRateNum)
            MostrarPnl(row.ProfitLoss)
            txtDinOps.Text = String.Format("{0:N0} ops", row.TotalOps)
            txtDinDrawdown.Text = ""
            txtDinEstado.Text = String.Format("{0} | {1} / {2}", row.Estrategia, row.Symbol, row.Timeframe)
            txtDinVersion.Text = row.Estrategia & version

            Await CargarOperacionesSesionAsync(row.Id)
        End Sub

        ' ── Eliminar sesion seleccionada ──────────────────────────────────
        Private Async Sub btnDinEliminar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim row = TryCast(lstDinSesiones.SelectedItem, SesionRow)
            If row Is Nothing Then Return
            Dim confirm = System.Windows.MessageBox.Show(
                String.Format("Eliminar sesion {0}?{1}{2}{1}Esta accion borrara {3:N0} operaciones de la BD.",
                              row.IdCorto, Environment.NewLine, row.Resumen, row.TotalOps),
                "Confirmar eliminacion",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning)
            If confirm <> System.Windows.MessageBoxResult.Yes Then Return
            Try
                btnDinEliminar.IsEnabled = False
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim body = New JObject From {{"sesion_id", row.Id}}
                    Dim content As New StringContent(body.ToString(), Text.Encoding.UTF8, "application/json")
                    Dim resp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/sesiones/eliminar", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await resp.Content.ReadAsStringAsync())
                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        ActualizarLogBD(String.Format("Sesion {0} eliminada correctamente.", row.IdCorto), Colors.LightGreen)
                        dgDinOperaciones.ItemsSource = Nothing
                        brdDinConfig.Visibility = System.Windows.Visibility.Collapsed
                        ' Refrescar la lista
                        Dim nombreSel = If(lstEstrategias.SelectedItem?.ToString(), "")
                        Dim filtro = If(chkDinTodasEstrategias?.IsChecked = True, "", nombreSel)
                        Await CargarSesionesAsync(filtro)
                    Else
                        ActualizarLogBD("Error: " & json("error")?.ToString(), Colors.Salmon)
                        btnDinEliminar.IsEnabled = True
                    End If
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error eliminando: " & ex.Message, Colors.Salmon)
                btnDinEliminar.IsEnabled = True
            End Try
        End Sub

        ' ── Cargar operaciones de sesion (ultimas 500) ───────────────────
        Private Async Function CargarOperacionesSesionAsync(sesionId As Integer) As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Dim limite = 500
                Dim url = String.Format("http://localhost:{0}/api/dinamicas/operaciones?sesion_id={1}&limite={2}",
                                        _apiServer.Port, sesionId, limite)
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim resp = Await client.GetStringAsync(url)
                    Dim json = JObject.Parse(resp)
                    Dim arr = TryCast(json("operaciones"), JArray)
                    If arr IsNot Nothing Then
                        Dim totalBD As Long = If(json("total") IsNot Nothing, CLng(json("total")), CLng(arr.Count))
                        Dim mostrando As Integer = If(json("mostrando") IsNot Nothing, CInt(json("mostrando")), arr.Count)
                        Dim lista As New List(Of OperacionRow)()
                        For Each item As JObject In arr
                            Dim pnlVal = CDec(item("pnl"))
                            Dim balPost = If(item("balance_post") IsNot Nothing AndAlso item("balance_post").Type <> JTokenType.Null,
                                             CDec(item("balance_post")), 0D)
                            Dim epSal As Long = If(item("epoch_salida") IsNot Nothing AndAlso item("epoch_salida").Type <> JTokenType.Null,
                                                   CLng(item("epoch_salida")), 0L)
                            lista.Add(New OperacionRow With {
                                .Estrategia = item("estrategia")?.ToString(),
                                .Version = item("version")?.ToString(),
                                .Tipo = item("tipo")?.ToString(),
                                .Epoch = DateTimeOffset.FromUnixTimeSeconds(CLng(item("epoch"))).ToString("dd/MM/yyyy HH:mm"),
                                .EpochSalida = If(epSal > 0,
                                                  DateTimeOffset.FromUnixTimeSeconds(epSal).ToString("dd/MM/yyyy HH:mm"),
                                                  ""),
                                .Entrada = String.Format("{0:F5}", CDec(item("entrada"))),
                                .Salida = String.Format("{0:F5}", CDec(item("salida"))),
                                .Estado = item("estado")?.ToString(),
                                .Pnl = String.Format("{0:+0.00;-0.00;0.00}", pnlVal),
                                .Confianza = String.Format("{0}%", CInt(item("confianza"))),
                                .BalanceInicio = "",
                                .Balance = If(balPost > 0, String.Format("${0:N2}", balPost), ""),
                                .Cuenta = item("cuenta")?.ToString()
                            })
                        Next
                        dgDinOperaciones.ItemsSource = lista
                        ' Mostrar aviso si hay mas operaciones de las que se muestran
                        If totalBD > mostrando Then
                            ActualizarLogBD(String.Format(
                                "Mostrando ultimas {0:N0} de {1:N0} operaciones en sesion #{2}. " &
                                "Usa run_backtest.ps1 para ver todas via SQL.",
                                mostrando, totalBD, sesionId), Colors.CornflowerBlue)
                        Else
                            ActualizarLogBD(String.Format(
                                "Sesion #{0} — {1:N0} operaciones cargadas.", sesionId, mostrando),
                                Colors.LightGreen)
                        End If
                    End If
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error cargando operaciones: " & ex.Message, Colors.Salmon)
            End Try
        End Function

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
            If _tradingWs Is Nothing OrElse _tradingWs.State <> System.Net.WebSockets.WebSocketState.Open Then
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
            Dim cuentaTxt = If(_tradingEsDemo, "DEMO (virtual)", "REAL")
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
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim content As New StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    Dim httpResp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/iniciar", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await httpResp.Content.ReadAsStringAsync())

                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        _dinLiveActivo = True
                        _dinReplayMotor = Nothing
                        _dinUltimoSesionId = CInt(json("sesion_id"))
                        Dim cuentaTag = If(_tradingEsDemo, "DEMO", "REAL")
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
                Using client As New HttpClient()
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

    ' ── DTO para la lista de sesiones ────────────────────────────────────
    Public Class SesionRow
        Public Property Id As Integer
        Public Property IdCorto As String          ' "#38"
        Public Property FechaHora As String          ' "28/04 14:32"
        Public Property WinRate As String          ' "62.5%"
        Public Property WinBrush As SolidColorBrush
        Public Property Resumen As String          ' "EmaRsiCruce · R_100/1h · 142ops · +$85.20"
        ' Campos de configuracion (para la barra de detalle al seleccionar)
        Public Property Estrategia As String
        Public Property Symbol As String
        Public Property Timeframe As String
        Public Property DuracionV As Integer
        Public Property PayoutPct As Decimal
        Public Property MinConf As Integer
        Public Property DesdeStr As String
        Public Property HastaStr As String
        Public Property TotalSenales As Long
        Public Property TotalOps As Long
        Public Property ProfitLoss As Decimal
        Public Property ProfitFactor As Decimal
        Public Property WinRateNum As Decimal         ' valor numerico para colorear
    End Class

    ' ── DTO para el DataGrid ─────────────────────────────────────────────
    Public Class OperacionRow
        Public Property Estrategia As String
        Public Property Version As String
        Public Property Tipo As String
        Public Property Epoch As String   ' apertura formateada "dd/MM/yyyy HH:mm"
        Public Property EpochSalida As String   ' cierre formateado  "dd/MM/yyyy HH:mm"
        Public Property Entrada      As String   ' formateado "{0:F5}"
        Public Property Salida       As String   ' formateado "{0:F5}"
        Public Property Estado       As String
        Public Property Pnl          As String   ' formateado "{0:+0.00;-0.00}"
        Public Property Confianza    As String   ' formateado "xx%"
        Public Property BalanceInicio As String
        Public Property Balance      As String
        Public Property Cuenta       As String
    End Class

End Namespace
