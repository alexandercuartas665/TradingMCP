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

    End Class

End Namespace
