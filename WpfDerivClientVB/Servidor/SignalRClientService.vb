' ============================================================
' SignalRClientService.vb
'
' Cliente SignalR singleton thread-safe adaptado del patron del
' proyecto Doom (DroneClientService.vb). Mantiene la conexion al
' servidor de estrategias, recibe instrucciones (estrategia.iniciar,
' estrategia.detener, etc.) y reporta estado de motores activos.
'
' Si el servidor cae:
'   - Las estrategias en LiveStrategiesController NO se detienen.
'   - Un DispatcherTimer reintenta conectar cada 10 segundos.
'   - Cuando reconecta, vuelve a registrarse y reanuda reporting.
'
' Hub name por defecto: "StrategiesHub" — confirmar con el servidor.
' Eventos del servidor (5-string protocol del estilo Doom):
'   recibirInstruccion(clientId, instruccion, parametros, correlationId, timestamp)
'   registroConfirmado(clientId, connectionId, timestamp)
'   actualizarEstado(clientId, statusJson, timestamp)
'   clienteConectado / clienteDesconectado
' ============================================================
Imports System.Threading.Tasks
Imports System.Windows.Threading
Imports Microsoft.AspNet.SignalR.Client

Namespace WpfDerivClientVB.Servidor

    Public Class SignalRClientService

#Region "Singleton"
        Private Shared _instance As SignalRClientService
        Private Shared ReadOnly _lockObj As New Object()

        Public Shared Function GetInstance() As SignalRClientService
            If _instance Is Nothing Then
                SyncLock _lockObj
                    If _instance Is Nothing Then
                        _instance = New SignalRClientService()
                    End If
                End SyncLock
            End If
            Return _instance
        End Function
#End Region

#Region "Configuracion del Hub"
        ''' <summary>Nombre default del hub en el servidor SignalR. Configurable via ConectarAsync(url, clientId, hubName) o ConfigManager.SignalRHubName.</summary>
        Public Const HUB_NAME_DEFAULT As String = "DronesHub"

        ''' <summary>Nombre del hub usado en la ultima conexion exitosa.</summary>
        Public ReadOnly Property HubName As String
            Get
                Return If(_hubName, HUB_NAME_DEFAULT)
            End Get
        End Property

        Private _hubName As String = HUB_NAME_DEFAULT
#End Region

#Region "Variables y Propiedades"
        Private _connection As HubConnection
        Private _hubProxy As IHubProxy
        Private _serverUrl As String = ""
        Private _clientId As String = ""
        Private _reconnectTimer As DispatcherTimer
        Private _isConnecting As Boolean = False

        Public ReadOnly Property Estado As String
            Get
                If _connection Is Nothing Then Return "Desconectado"
                Return _connection.State.ToString()
            End Get
        End Property

        Public ReadOnly Property ClientId As String
            Get
                Return _clientId
            End Get
        End Property

        Public ReadOnly Property ServerUrl As String
            Get
                Return _serverUrl
            End Get
        End Property

        Public ReadOnly Property EstaConectado As Boolean
            Get
                Return _connection IsNot Nothing AndAlso _connection.State = ConnectionState.Connected
            End Get
        End Property
#End Region

#Region "Eventos publicos"
        ''' <summary>
        ''' correlationId vacio = fire-and-forget. Si tiene valor, hay que
        ''' responder via ResponderInstruccionAsync(correlationId, respuestaJson).
        ''' </summary>
        Public Event InstruccionRecibida(ByVal clientId As String, ByVal instruccion As String,
                                         ByVal parametros As String, ByVal correlationId As String,
                                         ByVal timestamp As String)
        Public Event BroadcastRecibido(ByVal instruccion As String, ByVal parametros As String, ByVal timestamp As String)
        Public Event EstadoConexionCambiado(ByVal nuevoEstado As String)
        Public Event RegistroConfirmado(ByVal clientId As String, ByVal connectionId As String, ByVal timestamp As String)
        Public Event ClienteConectado(ByVal clientId As String, ByVal timestamp As String)
        Public Event ClienteDesconectado(ByVal clientId As String, ByVal timestamp As String)
#End Region

#Region "Init"
        Private Sub New()
            _reconnectTimer = New DispatcherTimer With {
                .Interval = TimeSpan.FromSeconds(10)
            }
            AddHandler _reconnectTimer.Tick, AddressOf ReconectarTick
        End Sub
#End Region

#Region "Conexion"
        Public Async Function ConectarAsync(serverUrl As String, clientId As String,
                                             Optional hubName As String = "") As Task
            If _isConnecting Then Return
            If String.IsNullOrWhiteSpace(serverUrl) Then
                RaiseEvent EstadoConexionCambiado("Error: URL vacia")
                Return
            End If
            If String.IsNullOrWhiteSpace(clientId) Then
                RaiseEvent EstadoConexionCambiado("Error: ClientId vacio")
                Return
            End If
            If String.IsNullOrWhiteSpace(hubName) Then hubName = HUB_NAME_DEFAULT

            _isConnecting = True
            _serverUrl = serverUrl
            _clientId = clientId
            _hubName = hubName

            Try
                ' TLS 1.2 obligatorio para muchos servidores HTTPS modernos sobre IIS.
                ' Sin esto, SignalR negotiate puede dar 500 / handshake error.
                Try
                    Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12 Or
                                                                 Net.SecurityProtocolType.Tls11 Or
                                                                 Net.SecurityProtocolType.Tls
                Catch
                End Try

                LimpiarConexion()

                _connection = New HubConnection(serverUrl)
                _connection.TraceLevel = TraceLevels.None

                AddHandler _connection.StateChanged, AddressOf ConexionStateChanged
                AddHandler _connection.Closed, AddressOf ConexionCerrada
                AddHandler _connection.Error, AddressOf ConexionError

                _hubProxy = _connection.CreateHubProxy(hubName)

                ' 5-string protocol identico a Doom
                _hubProxy.On(Of String, String, String, String, String)(
                    "recibirInstruccion", AddressOf OnRecibirInstruccion)
                _hubProxy.On(Of String, String, String)(
                    "registroConfirmado", AddressOf OnRegistroConfirmado)
                _hubProxy.On(Of String, String, String)(
                    "actualizarEstado", AddressOf OnActualizarEstado)
                _hubProxy.On(Of String, String)(
                    "clienteConectado", AddressOf OnClienteConectado)
                _hubProxy.On(Of String, String)(
                    "clienteDesconectado", AddressOf OnClienteDesconectado)

                Await _connection.Start()

                ' Registrar este cliente en el servidor
                Await _hubProxy.Invoke("Register", clientId)

                _reconnectTimer.Stop()

            Catch ex As Exception
                RaiseEvent EstadoConexionCambiado("Error: " & ex.Message)
                If Not _reconnectTimer.IsEnabled Then
                    _reconnectTimer.Start()
                End If
            Finally
                _isConnecting = False
            End Try
        End Function

        Public Sub Desconectar()
            _reconnectTimer.Stop()
            LimpiarConexion()
            RaiseEvent EstadoConexionCambiado("Desconectado")
        End Sub

        Private Sub LimpiarConexion()
            If _connection IsNot Nothing Then
                Try
                    RemoveHandler _connection.StateChanged, AddressOf ConexionStateChanged
                    RemoveHandler _connection.Closed, AddressOf ConexionCerrada
                    RemoveHandler _connection.Error, AddressOf ConexionError
                    _connection.Dispose()
                Catch
                End Try
                _connection = Nothing
                _hubProxy = Nothing
            End If
        End Sub
#End Region

#Region "Mensajeria saliente"
        ''' <summary>Reportar estado del cliente al servidor (sin respuesta esperada).</summary>
        Public Async Function ReportarEstadoAsync(statusJson As String) As Task
            If Not EstaConectado Then Return
            Try
                Await _hubProxy.Invoke("ReportStatus", _clientId, statusJson)
            Catch ex As Exception
                ' No relanzar (el reconnect timer se encarga si la conexion cae),
                ' pero dejar constancia para diagnostico.
                AppLogger.Warn("[SignalR] ReportStatus fallo: " & ex.Message)
            End Try
        End Function

        ''' <summary>Responder una instruccion que trajo correlationId.</summary>
        Public Async Function ResponderInstruccionAsync(correlationId As String, respuestaJson As String) As Task
            If Not EstaConectado OrElse String.IsNullOrEmpty(correlationId) Then Return
            Try
                Await _hubProxy.Invoke("InformarRespuesta", correlationId, respuestaJson)
            Catch ex As Exception
                AppLogger.Warn("[SignalR] InformarRespuesta(corrId=" & correlationId & ") fallo: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Envia una instruccion al servidor para que la enrute a otro cliente
        ''' (o a si mismo). Util para pruebas y para coordinar entre clientes.
        ''' En el servidor existente este metodo es SendInstruction(droneId, instruccion, parametros).
        ''' </summary>
        Public Async Function EnviarInstruccionAsync(targetClientId As String, instruccion As String,
                                                      parametrosJson As String) As Task(Of Boolean)
            If Not EstaConectado Then Return False
            Try
                Await _hubProxy.Invoke("SendInstruction", targetClientId, instruccion, parametrosJson)
                Return True
            Catch ex As Exception
                AppLogger.Warn("[SignalR] SendInstruction(" & targetClientId & ", " & instruccion & ") fallo: " & ex.Message)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Solicita al servidor la lista de drones/clientes conectados actualmente.
        ''' Devuelve el JSON crudo que retorna el hub (lo que sea que SendInstruction devuelva).
        ''' </summary>
        Public Async Function GetConnectedDronesAsync() As Task(Of String)
            If Not EstaConectado Then Return ""
            Try
                Dim resp = Await _hubProxy.Invoke(Of Object)("GetConnectedDrones")
                If resp Is Nothing Then Return ""
                Return resp.ToString()
            Catch ex As Exception
                Return "Error: " & ex.Message
            End Try
        End Function
#End Region

#Region "Reconexion automatica"
        Private Async Sub ReconectarTick(sender As Object, e As EventArgs)
            If _serverUrl = "" OrElse _clientId = "" Then Return
            If _isConnecting OrElse EstaConectado Then Return
            Await ConectarAsync(_serverUrl, _clientId)
        End Sub

        Private Sub ConexionStateChanged(stateChange As StateChange)
            RaiseEvent EstadoConexionCambiado(stateChange.NewState.ToString())
            If stateChange.NewState = ConnectionState.Disconnected Then
                If Not _reconnectTimer.IsEnabled Then _reconnectTimer.Start()
            End If
        End Sub

        Private Sub ConexionCerrada()
            RaiseEvent EstadoConexionCambiado("Disconnected")
            If Not _reconnectTimer.IsEnabled Then _reconnectTimer.Start()
        End Sub

        Private Sub ConexionError(ex As Exception)
            RaiseEvent EstadoConexionCambiado("Error")
        End Sub
#End Region

#Region "Mensajeria entrante"
        Private Sub OnRecibirInstruccion(clientId As String, instruccion As String,
                                          parametros As String, correlationId As String,
                                          timestamp As String)
            If clientId = "ALL" Then
                RaiseEvent BroadcastRecibido(instruccion, parametros, timestamp)
            Else
                RaiseEvent InstruccionRecibida(clientId, instruccion, parametros, correlationId, timestamp)
            End If
        End Sub

        Private Sub OnRegistroConfirmado(clientId As String, connectionId As String, timestamp As String)
            RaiseEvent RegistroConfirmado(clientId, connectionId, timestamp)
        End Sub

        Private Sub OnActualizarEstado(clientId As String, statusJson As String, timestamp As String)
            ' No tenemos uso interno hoy — se podria propagar a UI si se quiere ver
            ' el estado de otros clientes conectados. Por ahora se ignora.
        End Sub

        Private Sub OnClienteConectado(clientId As String, timestamp As String)
            RaiseEvent ClienteConectado(clientId, timestamp)
        End Sub

        Private Sub OnClienteDesconectado(clientId As String, timestamp As String)
            RaiseEvent ClienteDesconectado(clientId, timestamp)
        End Sub
#End Region

    End Class

End Namespace
