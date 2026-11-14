' ============================================================
' DerivDatos.vb
'
' Consultas de datos de mercado (velas, simbolos) por la conexion
' autenticada de DerivTradingService. La API publica antigua
' (wss://ws.derivws.com, sin login) ya no existe: todo dato de
' Deriv necesita la cuenta conectada (Demo basta).
' ============================================================
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Namespace WpfDerivClientVB

    Public Module DerivDatos

        Public Const MENSAJE_SIN_CONEXION As String =
            "Sin conexion a Deriv: conecta la cuenta (Demo basta) en la pestana Trading o con POST /api/heavy/trading/conectar."

        ''' <summary>Consulta de solo lectura; si no hay conexion devuelve {ok:false, error} con un mensaje claro.</summary>
        Public Function ConsultarAsync(req As JObject, Optional timeoutMs As Integer = 15000) As Task(Of JObject)
            If Not DerivTradingService.Instance.EstaConectado Then
                Return Task.FromResult(New JObject From {{"ok", False}, {"error", MENSAJE_SIN_CONEXION}})
            End If
            Return DerivTradingService.Instance.ConsultarAsync(req, timeoutMs)
        End Function

        ''' <summary>Ultimas velas de un simbolo: {ok, candles} o {ok:false, error}.</summary>
        Public Function PedirVelasAsync(symbol As String, cantidad As Integer, granularidad As Integer,
                                        Optional timeoutMs As Integer = 15000) As Task(Of JObject)
            Return ConsultarAsync(New JObject From {
                {"ticks_history", symbol}, {"style", "candles"}, {"granularity", granularidad},
                {"count", cantidad}, {"end", "latest"}, {"adjust_start_time", 1}
            }, timeoutMs)
        End Function

        ''' <summary>Convierte el arreglo "candles" de Deriv en CandleModel (OpenTime en hora local).</summary>
        Public Function ACandles(velas As JArray) As List(Of CandleModel)
            Dim lista As New List(Of CandleModel)()
            If velas Is Nothing Then Return lista
            For Each tok As JToken In velas
                Dim epoch As Long = CLng(tok("epoch"))
                lista.Add(New CandleModel With {
                    .Epoch = epoch,
                    .OpenTime = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    .OpenPrice = CDec(tok("open")),
                    .High = CDec(tok("high")),
                    .Low = CDec(tok("low")),
                    .ClosePrice = CDec(tok("close"))
                })
            Next
            Return lista
        End Function

    End Module

End Namespace
