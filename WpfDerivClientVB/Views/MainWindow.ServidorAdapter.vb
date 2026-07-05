' ============================================================
' MainWindow.ServidorAdapter.vb
'
' Adapta MainWindow para que el modulo Servidor (cliente SignalR
' + LiveStrategiesController) pueda ejecutar trades en Deriv via
' los WebSocket que MainWindow ya mantiene abiertos.
'
' Pasos:
'   1) Implementa ITradingService delegando a las funciones Api*
'      privadas existentes en MainWindow.ApiServer.vb.
'   2) Se registra a si misma en TradingServiceLocator al cargar.
'
' Esto permite que LiveStrategiesController.IniciarAsync arranque
' un LiveTestMotor cuyo callback ExecuteTrade pase por aqui y
' termine en el WS de trading de MainWindow sin tener que mover
' codigo todavia.
' ============================================================
Imports System.Threading.Tasks
Imports System.Windows
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Namespace WpfDerivClientVB

    Partial Public Class MainWindow
        Implements ITradingService

        Private _servidorAdapterRegistrado As Boolean = False

        ''' <summary>
        ''' Llamar desde el constructor de MainWindow (al final de InitializeComponent)
        ''' o como handler del evento Loaded para registrar este servicio en el
        ''' singleton TradingServiceLocator. Idempotente.
        ''' </summary>
        Public Sub RegistrarComoTradingService()
            If _servidorAdapterRegistrado Then Return
            Try
                TradingServiceLocator.Register(Me)
                _servidorAdapterRegistrado = True
                AppLogger.Info("[Servidor] MainWindow registrado como ITradingService.")
            Catch ex As Exception
                AppLogger.Warn("[Servidor] No se pudo registrar TradingService: " & ex.Message)
            End Try
        End Sub

        ' ── ITradingService ─────────────────────────────────────────

        Public Function GetStatus() As JObject Implements ITradingService.GetStatus
            Return ApiGetStatus()
        End Function

        Public Function GetOperations() As JArray Implements ITradingService.GetOperations
            Return ApiGetOperations()
        End Function

        Public Function ExecuteTradeAsync(body As JObject) As Task(Of JObject) Implements ITradingService.ExecuteTradeAsync
            Return ApiExecuteTrade(body)
        End Function

        Public Function ExecuteSellAsync(contractId As String) As Task(Of JObject) Implements ITradingService.ExecuteSellAsync
            Return ApiExecuteSell(contractId)
        End Function

        ''' <summary>
        ''' Conecta el WS de trading sin clicks: selecciona el cliente en el combo
        ''' (por nombre o el primero disponible), fuerza tipo de cuenta demo/real,
        ''' dispara la misma logica del boton Conectar y espera hasta 20s a que el
        ''' socket quede abierto. Pensado para el modo agente (endpoint HTTP).
        ''' </summary>
        Public Async Function ConectarTradingAsync(esDemo As Boolean,
                                                    Optional nombreCliente As String = "") As Task(Of JObject) _
            Implements ITradingService.ConectarTradingAsync

            ' Ya conectado → devolver estado actual
            If _tradingWs IsNot Nothing AndAlso _tradingWs.State = Net.WebSockets.WebSocketState.Open Then
                Return New JObject From {{"ok", True}, {"mensaje", "Ya conectado."}, {"estado", ApiGetStatus()}}
            End If

            ' 1) Seleccionar cliente + tipo de cuenta en el UI thread
            Dim seleccionOk As Boolean = False
            Dim errorSel As String = ""
            Await Dispatcher.InvokeAsync(
                Sub()
                    Try
                        If cmbTradingClientes.Items.Count = 0 Then
                            errorSel = "No hay clientes cargados en el combo."
                            Return
                        End If
                        Dim objetivo As ClientModel = Nothing
                        If Not String.IsNullOrWhiteSpace(nombreCliente) Then
                            For Each it In cmbTradingClientes.Items
                                Dim c = TryCast(it, ClientModel)
                                If c IsNot Nothing AndAlso c.Name.ToLowerInvariant().Contains(nombreCliente.ToLowerInvariant()) Then
                                    objetivo = c
                                    Exit For
                                End If
                            Next
                            If objetivo Is Nothing Then
                                errorSel = "Cliente '" & nombreCliente & "' no encontrado."
                                Return
                            End If
                        Else
                            objetivo = TryCast(cmbTradingClientes.Items(0), ClientModel)
                        End If
                        cmbTradingClientes.SelectedItem = objetivo
                        cmbTradingTipoCuenta.SelectedIndex = If(esDemo, 0, 1)
                        seleccionOk = True
                    Catch ex As Exception
                        errorSel = ex.Message
                    End Try
                End Sub)
            If Not seleccionOk Then
                Return New JObject From {{"ok", False}, {"error", "Seleccion de cliente: " & errorSel}}
            End If

            ' 2) Esperar a que SelectionChanged cargue las credenciales (async en background)
            For i = 1 To 30
                If _tradingCredsActual IsNot Nothing Then Exit For
                Await Task.Delay(200)
            Next
            If _tradingCredsActual Is Nothing Then
                Return New JObject From {{"ok", False}, {"error", "El cliente no tiene credenciales Deriv cargadas."}}
            End If

            ' 3) Disparar la logica del boton Conectar (async void: fire) y esperar el socket
            Await Dispatcher.InvokeAsync(Sub() btnConectarTrading_Click(Nothing, Nothing))
            For i = 1 To 40   ' hasta 20s
                If _tradingWs IsNot Nothing AndAlso _tradingWs.State = Net.WebSockets.WebSocketState.Open Then
                    Exit For
                End If
                Await Task.Delay(500)
            Next

            Dim conectado = _tradingWs IsNot Nothing AndAlso _tradingWs.State = Net.WebSockets.WebSocketState.Open
            Return New JObject From {
                {"ok", conectado},
                {"connected", conectado},
                {"demo", esDemo},
                {"cliente", If(_tradingClienteActual?.Name, "")},
                {"error", If(conectado, "", "Timeout: el WS no abrio en 20s (revisar credenciales/OTP).")}
            }
        End Function

    End Class

End Namespace
