' ============================================================
' ITradingService.vb
'
' Contrato que MainWindow expone hacia el modulo Servidor para
' que las estrategias live puedan ejecutar trades en Deriv sin
' acoplarse directamente a la ventana.
'
' Implementado por DerivTradingService (Trading/DerivTradingService.vb),
' registrado en App.OnStartup via TradingServiceLocator.Register(...).
' No depende de ninguna ventana.
' ============================================================
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.Servidor

    Public Interface ITradingService
        ''' <summary>Devuelve el estado actual de la conexion + balance.</summary>
        Function GetStatus() As JObject

        ''' <summary>Lista de operaciones de la sesion actual de Deriv.</summary>
        Function GetOperations() As JArray

        ''' <summary>Ejecuta un trade nuevo (CALL/PUT). El body incluye contract_type, symbol, amount, duration.</summary>
        Function ExecuteTradeAsync(body As JObject) As Task(Of JObject)

        ''' <summary>Cierra/vende un contrato abierto por contractId.</summary>
        Function ExecuteSellAsync(contractId As String) As Task(Of JObject)

        ''' <summary>
        ''' Conecta el WebSocket de trading de Deriv sin interaccion de UI.
        ''' esDemo=True usa el token virtual. nombreCliente vacio = primer cliente
        ''' con credenciales configuradas. Devuelve {ok, connected, balance,...}.
        ''' </summary>
        Function ConectarTradingAsync(esDemo As Boolean, Optional nombreCliente As String = "") As Task(Of JObject)
    End Interface

End Namespace
