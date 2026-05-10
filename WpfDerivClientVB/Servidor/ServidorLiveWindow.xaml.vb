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
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Partial Public Class ServidorLiveWindow
    Inherits Window

    Private ReadOnly _logEntries As New ObservableCollection(Of LogEntry)()
    Private ReadOnly _motorRows As New ObservableCollection(Of MotorRow)()
    Private _clockTimer As DispatcherTimer
    Private _signalR As SignalRClientService
    Private _ctrl As LiveStrategiesController

    Public Sub New()
        InitializeComponent()
        AddHandler Me.Loaded, AddressOf OnLoadedHandler
        AddHandler Me.Closing, AddressOf OnClosingHandler
        lstLog.ItemsSource = _logEntries
        lstMotores.ItemsSource = _motorRows
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

        AppendLog("info", "Ventana lista. Configura URL y Client ID si es la primera vez.")
        ActualizarPills()
        RefrescarMotores()

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
            RefrescarMotores()
        Catch ex As Exception
            AppendLog("err", "Error reanudando: " & ex.Message)
        End Try
    End Sub

    Private Sub btnEstado_Click(sender As Object, e As RoutedEventArgs)
        Dim estado = _ctrl.GetEstadoGlobal()
        AppendLog("info", "Estado global: " & estado.ToString(Newtonsoft.Json.Formatting.None))
        RefrescarMotores()
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
                                   RefrescarMotores()
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
                                   RefrescarMotores()
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

    Private Sub RefrescarMotores()
        _motorRows.Clear()
        For Each kv In _ctrl.Motores
            Try
                Dim est = kv.Value.GetEstado()
                _motorRows.Add(MotorRow.Desde(kv.Key, est))
            Catch
            End Try
        Next
        If _motorRows.Count = 0 Then
            lblMotoresResumen.Text = "Sin motores activos."
        Else
            lblMotoresResumen.Text = _motorRows.Count & " motor(es) activo(s)"
        End If
        pillMotores.Text = "Motores: " & _ctrl.Count
    End Sub

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

''' <summary>Tarjeta de motor activo (panel derecho).</summary>
Public Class MotorRow
    Public Property SesionId As Integer
    Public Property IdCorto As String
    Public Property Titulo As String
    Public Property SubTitulo As String
    Public Property OpsStr As String
    Public Property PnlStr As String
    Public Property PnlBrush As Brush
    Public Property EstadoStr As String
    Public Property EstadoBrush As Brush

    Public Shared Function Desde(sesionId As Integer, estado As JObject) As MotorRow
        Dim activo = TryGet(estado, "activo", False)
        Dim pausado = TryGet(estado, "pausado", False)
        Dim symbol = TryGetStr(estado, "symbol", "?")
        Dim tf = TryGetStr(estado, "timeframe", "?")
        Dim estr = TryGetStr(estado, "estrategia", "?")
        Dim ops = TryGetInt(estado, "total_operaciones", 0)
        Dim ganadas = TryGetInt(estado, "ganadas", 0)
        Dim perdidas = TryGetInt(estado, "perdidas", 0)
        Dim pnl = TryGetDec(estado, "pnl_total", 0D)

        Dim estStr = "EJECUTANDO"
        If pausado Then estStr = "PAUSADO"
        If Not activo Then estStr = "INACTIVO"

        Dim verde = New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
        Dim rojo = New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
        Dim ambar = New SolidColorBrush(Color.FromRgb(&HFF, &HB4, &H54))
        Dim gris = New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))

        Return New MotorRow With {
            .SesionId = sesionId,
            .IdCorto = "#" & sesionId,
            .Titulo = estr,
            .SubTitulo = symbol & " / " & tf,
            .OpsStr = String.Format("ops:{0}  ✓{1}  ✕{2}", ops, ganadas, perdidas),
            .PnlStr = pnl.ToString("+0.00;-0.00;0.00"),
            .PnlBrush = If(pnl > 0D, verde, If(pnl < 0D, rojo, gris)),
            .EstadoStr = estStr,
            .EstadoBrush = If(pausado, ambar, If(activo, verde, gris))
        }
    End Function

    Private Shared Function TryGet(j As JObject, key As String, def As Boolean) As Boolean
        If j Is Nothing OrElse j(key) Is Nothing Then Return def
        Dim v As Boolean
        If Boolean.TryParse(j(key).ToString(), v) Then Return v
        Return def
    End Function
    Private Shared Function TryGetStr(j As JObject, key As String, def As String) As String
        If j Is Nothing OrElse j(key) Is Nothing Then Return def
        Return j(key).ToString()
    End Function
    Private Shared Function TryGetInt(j As JObject, key As String, def As Integer) As Integer
        If j Is Nothing OrElse j(key) Is Nothing Then Return def
        Dim v As Integer
        If Integer.TryParse(j(key).ToString(), v) Then Return v
        Return def
    End Function
    Private Shared Function TryGetDec(j As JObject, key As String, def As Decimal) As Decimal
        If j Is Nothing OrElse j(key) Is Nothing Then Return def
        Dim v As Decimal
        If Decimal.TryParse(j(key).ToString().Replace(",", "."),
                             Globalization.NumberStyles.Any,
                             Globalization.CultureInfo.InvariantCulture, v) Then Return v
        Return def
    End Function
End Class
