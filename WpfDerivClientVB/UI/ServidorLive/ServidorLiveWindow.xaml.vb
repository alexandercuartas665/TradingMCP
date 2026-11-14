' ============================================================
' ServidorLiveWindow.xaml.vb
'
' Ventana hija que controla el cliente SignalR + visualiza el log
' de instrucciones recibidas y el estado en vivo de las estrategias
' que estan corriendo. Es solo UI: los servicios (SignalRClientService,
' LiveStrategiesController, CommandRouter) viven como singletons en
' la app y no se detienen al cerrar esta ventana.
' ============================================================
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Partial Public Class ServidorLiveWindow
    Inherits Window

    Private ReadOnly _logEntries As New ObservableCollection(Of LogEntry)()
    Private _clockTimer As DispatcherTimer
    Private _signalR As SignalRClientService
    Private _ctrl As LiveStrategiesController
    Private _panelLive As EstrategiasLivePanelControl

    Public Sub New()
        InitializeComponent()
        AddHandler Me.Loaded, AddressOf OnLoadedHandler
        AddHandler Me.Closing, AddressOf OnClosingHandler
        lstLog.ItemsSource = _logEntries
    End Sub

    Private Sub OnLoadedHandler(sender As Object, e As RoutedEventArgs)
        ' Reloj
        _clockTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
        AddHandler _clockTimer.Tick, Sub(s, ev) txtClock.Text = DateTime.UtcNow.ToString("HH:mm:ss") & " UTC"
        _clockTimer.Start()
        txtClock.Text = DateTime.UtcNow.ToString("HH:mm:ss") & " UTC"

        _signalR = SignalRClientService.GetInstance()
        _ctrl = LiveStrategiesController.GetInstance()
        _ctrl.LogAction = AddressOf AppendLogControl

        ' Cargar settings
        Dim cfg = ConfigManager.LoadDbConfig()
        txtUrl.Text = If(cfg.SignalRUrl, "")
        txtClientId.Text = If(String.IsNullOrWhiteSpace(cfg.SignalRClientId), Environment.MachineName, cfg.SignalRClientId)
        txtHubName.Text = If(String.IsNullOrWhiteSpace(cfg.SignalRHubName), SignalRClientService.HUB_NAME_DEFAULT, cfg.SignalRHubName)
        chkAuto.IsChecked = cfg.AutoConectarSignalR
        lblHubName.Text = "Hub: " & txtHubName.Text

        ' Suscribir eventos del cliente
        AddHandler _signalR.EstadoConexionCambiado, AddressOf OnEstadoConexion
        AddHandler _signalR.RegistroConfirmado, AddressOf OnRegistroConfirmado
        AddHandler _signalR.InstruccionRecibida, AddressOf OnInstruccionRecibida
        AddHandler _signalR.BroadcastRecibido, AddressOf OnBroadcastRecibido
        AddHandler _signalR.ClienteConectado, AddressOf OnClienteConectado
        AddHandler _signalR.ClienteDesconectado, AddressOf OnClienteDesconectado

        ' Suscribir motor updates
        AddHandler _ctrl.MotorActualizado, AddressOf OnMotorActualizado

        ' Cablear panel lateral de estrategias activas (reusa el control que
        ' MainWindow ya usa en panelEstrategiasLiveContainer). Es una instancia
        ' independiente: ambas ventanas pueden mostrar el panel sin chocar
        ' porque los datos viven en LiveStrategiesController (singleton).
        CablearPanelLive()

        AppendLog("info", "Ventana lista. Configura URL y Client ID si es la primera vez.")
        ActualizarPills()
        SincronizarPanelDesdeController()

        If chkAuto.IsChecked = True AndAlso Not String.IsNullOrWhiteSpace(txtUrl.Text) Then
            btnConectar_Click(Nothing, Nothing)
        End If
    End Sub

    Private Sub OnClosingHandler(sender As Object, e As System.ComponentModel.CancelEventArgs)
        ' NO desconectar — el cliente sigue activo en background como singleton.
        ' Solo desuscribir handlers locales para que no apunten a esta ventana.
        Try
            RemoveHandler _signalR.EstadoConexionCambiado, AddressOf OnEstadoConexion
            RemoveHandler _signalR.RegistroConfirmado, AddressOf OnRegistroConfirmado
            RemoveHandler _signalR.InstruccionRecibida, AddressOf OnInstruccionRecibida
            RemoveHandler _signalR.BroadcastRecibido, AddressOf OnBroadcastRecibido
            RemoveHandler _signalR.ClienteConectado, AddressOf OnClienteConectado
            RemoveHandler _signalR.ClienteDesconectado, AddressOf OnClienteDesconectado
            RemoveHandler _ctrl.MotorActualizado, AddressOf OnMotorActualizado
        Catch
        End Try
        If _clockTimer IsNot Nothing Then _clockTimer.Stop()
    End Sub

    ' ── BOTONES ──────────────────────────────────────────────────

    Private Async Sub btnConectar_Click(sender As Object, e As RoutedEventArgs)
        Dim url = txtUrl.Text.Trim()
        Dim cid = txtClientId.Text.Trim()
        Dim hub = txtHubName.Text.Trim()
        If String.IsNullOrEmpty(url) Then
            txtStatus.Text = "Debes ingresar una URL."
            Return
        End If
        If String.IsNullOrEmpty(cid) Then cid = Environment.MachineName
        If String.IsNullOrEmpty(hub) Then hub = SignalRClientService.HUB_NAME_DEFAULT

        ' Persistir lo ingresado para proxima vez
        Try
            Dim cfg = ConfigManager.LoadDbConfig()
            cfg.SignalRUrl = url
            cfg.SignalRClientId = cid
            cfg.SignalRHubName = hub
            cfg.AutoConectarSignalR = (chkAuto.IsChecked = True)
            ConfigManager.SaveDbConfig(cfg)
        Catch
        End Try

        lblHubName.Text = "Hub: " & hub
        AppendLog("conn", "Conectando a " & url & " (clientId=" & cid & ", hub=" & hub & ")...")
        txtStatus.Text = "Conectando..."
        btnConectar.IsEnabled = False
        Try
            Await _signalR.ConectarAsync(url, cid, hub)
        Catch ex As Exception
            AppendLog("err", "Error al conectar: " & ex.Message)
            txtStatus.Text = "Error: " & ex.Message
        Finally
            btnConectar.IsEnabled = True
        End Try
    End Sub

    Private Sub btnDesconectar_Click(sender As Object, e As RoutedEventArgs)
        Try
            _signalR.Desconectar()
            AppendLog("conn", "Desconectado por usuario.")
            txtStatus.Text = "Desconectado."
        Catch ex As Exception
            AppendLog("err", "Error al desconectar: " & ex.Message)
        End Try
    End Sub

    Private Async Sub btnReanudar_Click(sender As Object, e As RoutedEventArgs)
        AppendLog("info", "Reanudando sesiones live activas en BD...")
        Try
            Dim n = Await _ctrl.ReanudarActivasAsync()
            AppendLog("ok", n & " sesion(es) reanudadas en memoria.")
            SincronizarPanelDesdeController()
        Catch ex As Exception
            AppendLog("err", "Error reanudando: " & ex.Message)
        End Try
    End Sub

    Private Sub btnEstado_Click(sender As Object, e As RoutedEventArgs)
        Dim estado = _ctrl.GetEstadoGlobal()
        AppendLog("info", "Estado global: " & estado.ToString(Newtonsoft.Json.Formatting.None))
        SincronizarPanelDesdeController()
    End Sub

    Private Sub btnLimpiarLog_Click(sender As Object, e As RoutedEventArgs)
        _logEntries.Clear()
        lblLogResumen.Text = "0 lineas"
    End Sub

    ''' <summary>
    ''' Prueba 1: enviar un ReportStatus al servidor para verificar que el outbound
    ''' funciona. Si el servidor lo recibe deberias verlo en sus logs.
    ''' </summary>
    Private Async Sub btnPing_Click(sender As Object, e As RoutedEventArgs)
        If Not _signalR.EstaConectado Then
            AppendLog("err", "No estas conectado. Pulsa Conectar primero.")
            Return
        End If
        Dim status = New JObject From {
            {"tipo", "ping"},
            {"client_id", _signalR.ClientId},
            {"timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")},
            {"motores_activos", _ctrl.Count},
            {"mensaje", "Hola desde el cliente Trading Heavy"}
        }
        Try
            AppendLog("tx", "ReportStatus -> servidor: " & status.ToString(Newtonsoft.Json.Formatting.None))
            Await _signalR.ReportarEstadoAsync(status.ToString(Newtonsoft.Json.Formatting.None))
            AppendLog("ok", "ReportStatus enviado.")
        Catch ex As Exception
            AppendLog("err", "Error en ReportStatus: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Prueba 2: simular una instruccion "estrategia.iniciar" entrante COMO SI viniera
    ''' del servidor. Permite validar todo el pipeline (router -> controller -> motor)
    ''' sin depender de que el servidor envie algo. Usa DemoSimple/R_100/1m por defecto.
    ''' </summary>
    Private Sub btnSimular_Click(sender As Object, e As RoutedEventArgs)
        Dim cid = If(_signalR.ClientId, "BITCODEV1")
        Dim corrId = "test-" & Guid.NewGuid().ToString().Substring(0, 8)
        Dim payload = New JObject From {
            {"estrategia", "DemoSimple"},
            {"symbol", "R_100"},
            {"timeframe", "1m"},
            {"confianza", 50},
            {"payout", 85},
            {"duracion", 3},
            {"monto", 1.0},
            {"ventana_indicadores", 100}
        }
        AppendLog("rx", "[SIMULADO] estrategia.iniciar  payload=" & payload.ToString(Newtonsoft.Json.Formatting.None) & "  corrId=" & corrId)
        Try
            CommandRouter.Handle(cid, "estrategia.iniciar",
                                  payload.ToString(Newtonsoft.Json.Formatting.None),
                                  corrId,
                                  DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
            AppendLog("info", "Instruccion enviada al CommandRouter. Mira el panel derecho para ver el motor.")
        Catch ex As Exception
            AppendLog("err", "Error en simulacion: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Invoca GetConnectedDrones() en el hub real para verificar comunicacion
    ''' bidireccional sobre WebSocket.
    ''' </summary>
    Private Async Sub btnListar_Click(sender As Object, e As RoutedEventArgs)
        If Not _signalR.EstaConectado Then
            AppendLog("err", "No estas conectado.")
            Return
        End If
        AppendLog("tx", "Invocando GetConnectedDrones() en el hub...")
        Try
            Dim resp = Await _signalR.GetConnectedDronesAsync()
            AppendLog("ok", "Conectados (server): " & If(String.IsNullOrEmpty(resp), "(vacio)", resp))
        Catch ex As Exception
            AppendLog("err", "Error: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' Round-trip via WebSocket: invoca SendInstruction(self, "ping", "...") en el
    ''' servidor. El hub deberia enrutarla de vuelta a este mismo cliente, y
    ''' OnInstruccionRecibida debe loguearla. Valida WebSocket bidireccional
    ''' end-to-end con tu servidor real.
    ''' </summary>
    Private Async Sub btnRoundTrip_Click(sender As Object, e As RoutedEventArgs)
        If Not _signalR.EstaConectado Then
            AppendLog("err", "No estas conectado.")
            Return
        End If
        Dim target = If(txtTargetId.Text, "").Trim()
        If String.IsNullOrEmpty(target) Then target = "DronesHub"
        Dim cid = _signalR.ClientId
        Dim corrId = "rt-" & Guid.NewGuid().ToString().Substring(0, 8)
        Dim payload = New JObject From {
            {"mensaje", "round-trip test desde " & cid},
            {"timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")},
            {"correlationId", corrId}
        }
        AppendLog("tx", "SendInstruction(target=" & target & ", 'ping', " & payload.ToString(Newtonsoft.Json.Formatting.None) & ")")
        Try
            Dim ok = Await _signalR.EnviarInstruccionAsync(target, "ping",
                                                            payload.ToString(Newtonsoft.Json.Formatting.None))
            If ok Then
                AppendLog("ok", "SendInstruction invocada OK. Si el server enruta, veras la instruccion como RX.")
            Else
                AppendLog("err", "SendInstruction fallo en el hub.")
            End If
        Catch ex As Exception
            AppendLog("err", "Error: " & ex.Message)
        End Try
    End Sub

    ' ── HANDLERS DEL CLIENTE SIGNALR ─────────────────────────────

    Private Sub OnEstadoConexion(nuevoEstado As String)
        Dispatcher.BeginInvoke(Sub()
                                   AppendLog("conn", "Estado: " & nuevoEstado)
                                   ActualizarPills()
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub OnRegistroConfirmado(clientId As String, connectionId As String, timestamp As String)
        Dispatcher.BeginInvoke(Sub()
                                   AppendLog("ok", String.Format(
                                       "Registro confirmado: clientId={0} connId={1}", clientId, connectionId))
                                   ActualizarPills()
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub OnInstruccionRecibida(clientId As String, instruccion As String,
                                       parametros As String, correlationId As String, timestamp As String)
        Dispatcher.BeginInvoke(Sub()
                                   AppendLog("rx", String.Format("[{0}] {1}  payload={2}  corrId={3}",
                                              clientId, instruccion, parametros, correlationId))
                                   SincronizarPanelDesdeController()
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub OnBroadcastRecibido(instruccion As String, parametros As String, timestamp As String)
        Dispatcher.BeginInvoke(Sub()
                                   AppendLog("rx", String.Format("[BROADCAST] {0}  payload={1}",
                                              instruccion, parametros))
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub OnClienteConectado(clientId As String, timestamp As String)
        Dispatcher.BeginInvoke(Sub() AppendLog("info", "Cliente entro al hub: " & clientId),
                               DispatcherPriority.Background)
    End Sub

    Private Sub OnClienteDesconectado(clientId As String, timestamp As String)
        Dispatcher.BeginInvoke(Sub() AppendLog("info", "Cliente salio del hub: " & clientId),
                               DispatcherPriority.Background)
    End Sub

    Private Sub OnMotorActualizado(sesionId As Integer, estado As JObject)
        Dispatcher.BeginInvoke(Sub()
                                   AppendLog("motor", "Sesion #" & sesionId & " actualizada.")
                                   ' Si la sesion sigue activa, upsertear en el panel; si no, removerla.
                                   Dim activo = JBool(estado, "activo")
                                   If activo Then
                                       UpsertCardDesdeMotor(sesionId, estado)
                                   Else
                                       _panelLive?.RemoverCard("live-" & sesionId)
                                   End If
                                   pillMotores.Text = "Motores: " & _ctrl.Count
                                   ' Opcional: reportar al servidor para que tenga PnL en vivo
                                   Try
                                       If _signalR.EstaConectado Then
                                           Dim _t = _signalR.ReportarEstadoAsync(estado.ToString(Newtonsoft.Json.Formatting.None))
                                       End If
                                   Catch
                                   End Try
                               End Sub, DispatcherPriority.Background)
    End Sub

    ' ── REFRESCAR UI ─────────────────────────────────────────────

    Private Sub ActualizarPills()
        Dim conectado = _signalR IsNot Nothing AndAlso _signalR.EstaConectado
        pillEstado.Text = If(_signalR Is Nothing, "Desconectado", _signalR.Estado)
        pillEstado.Foreground = If(conectado,
                                    New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A)),
                                    New SolidColorBrush(Color.FromRgb(&HFF, &HB4, &H54)))
        pillUrl.Text = "URL: " & If(_signalR Is Nothing, "-", If(_signalR.ServerUrl, "-"))
        pillCliente.Text = "Cliente: " & If(_signalR Is Nothing, "-", If(_signalR.ClientId, "-"))
        pillMotores.Text = "Motores: " & _ctrl.Count
    End Sub

    ' ── PANEL DE ESTRATEGIAS ACTIVAS (reusa EstrategiasLivePanelControl) ─

    ''' <summary>
    ''' Instancia el control, lo monta en el ContentControl del XAML y
    ''' conecta sus 4 eventos a LiveStrategiesController.
    ''' </summary>
    Private Sub CablearPanelLive()
        If panelEstrategiasLiveContainer Is Nothing OrElse _panelLive IsNot Nothing Then Return
        _panelLive = New EstrategiasLivePanelControl()
        panelEstrategiasLiveContainer.Content = _panelLive
        AddHandler _panelLive.VerDetalleSolicitado, AddressOf OnPanelVerDetalle
        AddHandler _panelLive.PausarReanudarSolicitado, AddressOf OnPanelPausarReanudar
        AddHandler _panelLive.ConfigGuardarSolicitada, AddressOf OnPanelConfigGuardar
        AddHandler _panelLive.DetenerSolicitado, AddressOf OnPanelDetener
    End Sub

    ''' <summary>Recorre todos los motores activos en el controller y los Upsertea en el panel.</summary>
    Private Sub SincronizarPanelDesdeController()
        If _panelLive Is Nothing Then Return
        For Each kv In _ctrl.Motores
            Try
                UpsertCardDesdeMotor(kv.Key, kv.Value.GetEstado())
            Catch
            End Try
        Next
        pillMotores.Text = "Motores: " & _ctrl.Count
    End Sub

    ''' <summary>Construye un EstrategiaActivaCard desde el JObject del motor y lo upsertea.</summary>
    Private Sub UpsertCardDesdeMotor(sesionId As Integer, est As JObject)
        If _panelLive Is Nothing OrElse est Is Nothing Then Return
        Dim id = "live-" & sesionId
        Dim activo = JBool(est, "activo")
        Dim pausado = JBool(est, "pausado")
        Dim estadoTxt = If(Not activo, "Detenida", If(pausado, "Pausada", "Activa"))

        Dim card As New EstrategiaActivaCard With {
            .Id = id,
            .Nombre = JStr(est, "estrategia"),
            .Symbol = JStr(est, "symbol"),
            .Timeframe = JStr(est, "timeframe"),
            .HoraInicio = DateTime.Now.AddHours(-JDec(est, "horas_transcurridas")),
            .Abiertas = JInt(est, "pendientes"),
            .Ganadas = JInt(est, "n_ganadas"),
            .Perdidas = JInt(est, "n_perdidas"),
            .Pnl = JDec(est, "pnl_total"),
            .VelasAnalizadas = JInt(est, "velas_analizadas"),
            .Monto = JDec(est, "monto_operacion"),
            .MinConfianza = JInt(est, "min_confianza"),
            .DuracionVelas = JInt(est, "duracion_velas"),
            .PayoutPct = JDec(est, "payout_pct"),
            .VentanaIndicadores = JInt(est, "ventana_indicadores"),
            .TotalSenales = JInt(est, "total_senales"),
            .HorasOperacion = JInt(est, "horas_operacion"),
            .LimiteGanancia = JDec(est, "limite_ganancia"),
            .LimitePerdida = JDec(est, "limite_perdida"),
            .HorasTranscurridas = JDec(est, "horas_transcurridas"),
            .Estado = estadoTxt
        }
        _panelLive.UpsertCard(card)
    End Sub

    ' ── Handlers del panel (delegan a LiveStrategiesController directo) ──

    Private Sub OnPanelVerDetalle(id As String)
        AppendLog("info", "Ver detalle: " & id)
    End Sub

    Private Async Sub OnPanelDetener(id As String)
        Dim sid = ParseSesionIdFromCardId(id)
        If sid <= 0 Then
            _panelLive.RemoverCard(id)
            Return
        End If
        AppendLog("info", "Deteniendo sesion #" & sid & "...")
        Dim ok = Await _ctrl.DetenerAsync(sid)
        If ok Then
            _panelLive.RemoverCard(id)
            AppendLog("ok", "Sesion #" & sid & " detenida.")
        Else
            AppendLog("err", "No se pudo detener sesion #" & sid)
        End If
        pillMotores.Text = "Motores: " & _ctrl.Count
    End Sub

    Private Sub OnPanelPausarReanudar(id As String)
        Dim sid = ParseSesionIdFromCardId(id)
        If sid <= 0 Then Return
        Dim motor As LiveTestMotor = Nothing
        _ctrl.Motores.TryGetValue(sid, motor)
        If motor Is Nothing Then
            AppendLog("err", "Motor #" & sid & " no esta en memoria.")
            Return
        End If
        Dim pausarAhora = Not motor.EstaPausado   ' toggle
        Dim ok = _ctrl.PausarReanudar(sid, pausarAhora)
        If ok Then
            Dim estadoTxt = If(pausarAhora, "Pausada", "Activa")
            _panelLive.ActualizarMetricas(id, motor.GetEstado()("pendientes").ToObject(Of Integer)(),
                                           motor.GetEstado()("n_ganadas").ToObject(Of Integer)(),
                                           motor.GetEstado()("n_perdidas").ToObject(Of Integer)(),
                                           0D, 0D,
                                           motor.GetEstado()("pnl_total").ToObject(Of Decimal)(),
                                           estado:=estadoTxt)
            AppendLog("ok", "Sesion #" & sid & " -> " & estadoTxt)
        End If
    End Sub

    Private Sub OnPanelConfigGuardar(id As String, card As EstrategiaActivaCard)
        Dim sid = ParseSesionIdFromCardId(id)
        If sid <= 0 OrElse card Is Nothing Then Return
        Dim ok = _ctrl.ActualizarConfig(sid, card.Monto, card.MinConfianza, card.DuracionVelas,
                                         card.HorasOperacion, card.LimiteGanancia, card.LimitePerdida)
        If ok Then
            AppendLog("ok", String.Format("Sesion #{0} config aplicada: monto=${1:F2} conf={2}% dur={3}",
                                          sid, card.Monto, card.MinConfianza, card.DuracionVelas))
        Else
            AppendLog("err", "No se pudo aplicar config a sesion #" & sid)
        End If
    End Sub

    Private Shared Function ParseSesionIdFromCardId(id As String) As Integer
        If String.IsNullOrEmpty(id) OrElse Not id.StartsWith("live-") Then Return 0
        Dim n As Integer
        Integer.TryParse(id.Substring("live-".Length), n)
        Return n
    End Function

    ' Helpers para leer del JObject de GetEstado()
    Private Shared Function JStr(j As JObject, k As String) As String
        If j Is Nothing OrElse j(k) Is Nothing Then Return ""
        Return j(k).ToString()
    End Function
    Private Shared Function JInt(j As JObject, k As String) As Integer
        If j Is Nothing OrElse j(k) Is Nothing Then Return 0
        Dim v As Integer
        Return If(Integer.TryParse(j(k).ToString(), v), v, 0)
    End Function
    Private Shared Function JDec(j As JObject, k As String) As Decimal
        If j Is Nothing OrElse j(k) Is Nothing Then Return 0D
        Dim v As Decimal
        Return If(Decimal.TryParse(j(k).ToString().Replace(",", "."),
                                    Globalization.NumberStyles.Any,
                                    Globalization.CultureInfo.InvariantCulture, v), v, 0D)
    End Function
    Private Shared Function JBool(j As JObject, k As String) As Boolean
        If j Is Nothing OrElse j(k) Is Nothing Then Return False
        Dim v As Boolean
        Return Boolean.TryParse(j(k).ToString(), v) AndAlso v
    End Function

    ' ── LOG ──────────────────────────────────────────────────────

    Private Sub AppendLog(tipo As String, mensaje As String)
        If Not Dispatcher.CheckAccess() Then
            Dispatcher.BeginInvoke(Sub() AppendLog(tipo, mensaje), DispatcherPriority.Background)
            Return
        End If
        Dim entry As New LogEntry With {
            .Hora = DateTime.Now.ToString("HH:mm:ss"),
            .Tipo = tipo.ToUpperInvariant(),
            .Mensaje = mensaje
        }
        Select Case tipo.ToLowerInvariant()
            Case "ok" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
            Case "err" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            Case "rx" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&H5E, &HE0, &HD0))
            Case "tx" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&HFF, &HB4, &H54))
            Case "conn" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&H5C, &HB6, &HFF))
            Case "motor" : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&HC3, &HA9, &HFF))
            Case Else : entry.TipoBrush = New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
        End Select
        _logEntries.Add(entry)
        If _logEntries.Count > 500 Then _logEntries.RemoveAt(0)
        lblLogResumen.Text = _logEntries.Count & " lineas"
        ' auto-scroll al fondo
        Try
            scrLog.ScrollToEnd()
        Catch
        End Try
    End Sub

    ''' <summary>Adapter para que LiveStrategiesController.LogAction llegue al log de la UI.</summary>
    Private Sub AppendLogControl(msg As String)
        Try
            Dim s = If(msg, "")
            Dim tipo = If(s.Contains("ERROR") OrElse s.Contains("WARN"), "err", "info")
            AppendLog(tipo, s)
        Catch
        End Try
    End Sub

End Class

''' <summary>Fila del log central.</summary>
Public Class LogEntry
    Public Property Hora As String
    Public Property Tipo As String
    Public Property TipoBrush As Brush
    Public Property Mensaje As String
End Class

' Nota: la clase MotorRow se elimino. El panel derecho ahora usa
' EstrategiasLivePanelControl reusado de MainWindow.
