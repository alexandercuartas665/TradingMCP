' ============================================================
' MainWindow.DinPanelLive.vb
' Partial Class del MainWindow — Estrategias Dinamicas: panel lateral de sesiones live (tarjetas, pausar/detener, reanudar tras un reinicio, saldo y PnL).
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

                Using client As HttpClient = NuevoClienteApiLocal()
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
                        .VentanaIndicadores = 30,
                        .ModoForward = True
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
        ''' <summary>
        ''' HttpClient para las llamadas de esta pestana a la API local (8765), con el
        ''' header X-Agent-Token que exige desde el paso de seguridad (AgentAuth).
        ''' </summary>
        Private Shared Function NuevoClienteApiLocal() As HttpClient
            Dim c As New HttpClient()
            c.DefaultRequestHeaders.Add(AgentAuth.HeaderName, AgentAuth.GetOrCreateToken())
            Return c
        End Function

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

    End Class

End Namespace
