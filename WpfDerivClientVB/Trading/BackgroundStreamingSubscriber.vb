' ============================================================
' BackgroundStreamingSubscriber.vb
'
' Entrega las velas cerradas de cada (symbol, timeframe) que usan
' las estrategias live, sin cambiar el grafico visible.
'
' La API publica antigua de Deriv (ws.derivws.com) ya no responde,
' asi que en lugar de un WebSocket propio por activo se consultan
' las ultimas velas por la conexion autenticada de
' DerivTradingService (consulta de solo lectura en lista blanca):
' unos segundos despues de cada cierre se piden las ultimas velas
' y se emiten las cerradas que aun no se habian emitido. Si la
' conexion cae, el servicio reconecta y el sondeo sigue solo.
'
' Uso:
'   _multiSub = New BackgroundStreamingSubscriber()
'   _multiSub.OnCandleCerrada = Sub(c, sym, tf) ...
'   Await _multiSub.SubscribeAsync("frxUSDJPY", "1h", 3600)
'   Await _multiSub.UnsubscribeAsync("frxUSDJPY", "1h")
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Namespace WpfDerivClientVB

Public Class BackgroundStreamingSubscriber

    ''' <summary>Segundos de margen tras el cierre de la vela antes de consultarla.</summary>
    Private Const SEGUNDOS_TRAS_CIERRE As Integer = 3
    ''' <summary>Espera tras un error o sin conexion.</summary>
    Private Const SEGUNDOS_REINTENTO As Integer = 15
    ''' <summary>Espera cuando no hay vela abierta (mercado cerrado o datos atrasados).</summary>
    Private Const SEGUNDOS_SIN_VELA_ABIERTA As Integer = 60

    Private Class Suscripcion
        Public Symbol As String
        Public Timeframe As String
        Public Granularidad As Integer
        Public Inicializada As Boolean
        Public UltimaEmitida As Long          ' epoch de apertura de la ultima vela cerrada emitida
        Public ProximaConsulta As Long        ' epoch UTC en que toca consultar
        Public FallosSeguidos As Integer
        Public MercadoAbierto As Boolean      ' la ultima respuesta traia una vela abierta
        Public UltimaRespuestaUtc As Long     ' epoch de la ultima consulta correcta
    End Class

    Private ReadOnly _subs As New Dictionary(Of String, Suscripcion)()
    Private ReadOnly _lock As New Object()
    Private _cts As CancellationTokenSource
    Private _bucle As Task

    ''' <summary>Callback invocado cada vez que cierra una vela en cualquier suscripcion.</summary>
    Public Property OnCandleCerrada As Action(Of CandleModel, String, String)

    ''' <summary>Callback opcional para log de eventos del subscriber.</summary>
    Public Property LogAction As Action(Of String)

    ''' <summary>Consulta a Deriv. Por defecto la conexion autenticada; reemplazable en pruebas.</summary>
    Friend Property Consultar As Func(Of JObject, Task(Of JObject)) =
        Function(req) DerivTradingService.Instance.ConsultarAsync(req, 15000)

    ''' <summary>Reloj (epoch UTC). Reemplazable en pruebas.</summary>
    Friend Property AhoraEpoch As Func(Of Long) = Function() DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    Private Shared Function MakeKey(symbol As String, timeframe As String) As String
        Return (symbol & "/" & timeframe).ToLowerInvariant()
    End Function

    ''' <summary>Lista de suscripciones activas en formato "symbol/tf".</summary>
    Public ReadOnly Property Activas As List(Of String)
        Get
            SyncLock _lock
                Return _subs.Keys.ToList()
            End SyncLock
        End Get
    End Property

    ''' <summary>True si ya hay una suscripcion para (symbol, timeframe).</summary>
    Public Function EstaSuscrito(symbol As String, timeframe As String) As Boolean
        SyncLock _lock
            Return _subs.ContainsKey(MakeKey(symbol, timeframe))
        End SyncLock
    End Function

    ''' <summary>
    ''' Registra (symbol, timeframe). La primera consulta solo fija la ultima vela cerrada
    ''' (no emite historico); desde ahi se emite cada cierre nuevo.
    ''' </summary>
    Public Function SubscribeAsync(symbol As String, timeframe As String, granularity As Integer) As Task(Of Boolean)
        If String.IsNullOrWhiteSpace(symbol) OrElse String.IsNullOrWhiteSpace(timeframe) OrElse granularity <= 0 Then
            Return Task.FromResult(False)
        End If
        Dim key = MakeKey(symbol, timeframe)
        SyncLock _lock
            If Not _subs.ContainsKey(key) Then
                _subs(key) = New Suscripcion With {
                    .Symbol = symbol, .Timeframe = timeframe, .Granularidad = granularity, .ProximaConsulta = 0
                }
                Log(String.Format("Suscrito: {0}/{1} (consulta por la conexion autenticada)", symbol, timeframe))
            End If
            If _bucle Is Nothing OrElse _bucle.IsCompleted Then
                _cts = New CancellationTokenSource()
                Dim ct = _cts.Token
                _bucle = Task.Run(Function() BucleAsync(ct))
            End If
        End SyncLock
        If Not DerivTradingService.Instance.EstaConectado Then
            Log("AVISO: trading no conectado; las velas llegaran cuando se conecte (POST /api/heavy/trading/conectar).")
        End If
        Return Task.FromResult(True)
    End Function

    ''' <summary>Deja de entregar velas de (symbol, timeframe).</summary>
    Public Function UnsubscribeAsync(symbol As String, timeframe As String) As Task
        SyncLock _lock
            If _subs.Remove(MakeKey(symbol, timeframe)) Then
                Log(String.Format("Desuscrito: {0}/{1}", symbol, timeframe))
            End If
        End SyncLock
        Return Task.CompletedTask
    End Function

    ''' <summary>Quita todas las suscripciones y detiene el sondeo.</summary>
    Public Function UnsubscribeAllAsync() As Task
        SyncLock _lock
            _subs.Clear()
            If _cts IsNot Nothing Then
                Try
                    _cts.Cancel()
                Catch
                End Try
            End If
        End SyncLock
        Return Task.CompletedTask
    End Function

    Private Async Function BucleAsync(ct As CancellationToken) As Task
        Do While Not ct.IsCancellationRequested
            Dim aConsultar As List(Of Suscripcion)
            Dim ahora = AhoraEpoch.Invoke()
            SyncLock _lock
                aConsultar = _subs.Values.Where(Function(s) s.ProximaConsulta <= ahora).ToList()
            End SyncLock
            For Each s In aConsultar
                If ct.IsCancellationRequested Then Exit For
                Dim fallo As String = Nothing
                Try
                    Await ConsultarSuscripcionAsync(s)
                Catch ex As Exception
                    fallo = ex.Message
                End Try
                If fallo IsNot Nothing Then
                    s.FallosSeguidos += 1
                    s.ProximaConsulta = AhoraEpoch.Invoke() + SEGUNDOS_REINTENTO
                    If s.FallosSeguidos <= 3 OrElse s.FallosSeguidos Mod 40 = 0 Then
                        Log(String.Format("{0}/{1}: error consultando velas ({2})", s.Symbol, s.Timeframe, fallo))
                    End If
                End If
            Next
            Try
                Await Task.Delay(1000, ct)
            Catch ex As OperationCanceledException
                Exit Do
            End Try
        Loop
    End Function

    ''' <summary>Consulta las ultimas velas de una suscripcion, emite las nuevas cerradas y agenda la siguiente consulta.</summary>
    Private Async Function ConsultarSuscripcionAsync(s As Suscripcion) As Task
        Dim req As New JObject From {
            {"ticks_history", s.Symbol}, {"style", "candles"}, {"granularity", s.Granularidad},
            {"end", "latest"}, {"count", 5}, {"adjust_start_time", 1}
        }
        Dim r = Await Consultar.Invoke(req)
        Dim ahora = AhoraEpoch.Invoke()
        If r Is Nothing OrElse r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
            s.FallosSeguidos += 1
            s.ProximaConsulta = ahora + SEGUNDOS_REINTENTO
            If s.FallosSeguidos <= 3 OrElse s.FallosSeguidos Mod 40 = 0 Then
                Log(String.Format("{0}/{1}: sin velas ({2})", s.Symbol, s.Timeframe, If(r?("error")?.ToString(), "sin respuesta")))
            End If
            Return
        End If
        s.FallosSeguidos = 0
        s.UltimaRespuestaUtc = ahora
        Dim velas = TryCast(r("candles"), JArray)

        If Not s.Inicializada Then
            Dim cerradas = VelasCerradasNuevas(velas, s.Granularidad, ahora, 0)
            s.UltimaEmitida = If(cerradas.Count > 0, cerradas.Last().Epoch, 0)
            s.Inicializada = True
        Else
            For Each v In VelasCerradasNuevas(velas, s.Granularidad, ahora, s.UltimaEmitida)
                s.UltimaEmitida = v.Epoch
                Try
                    OnCandleCerrada?.Invoke(v, s.Symbol, s.Timeframe)
                Catch ex As Exception
                    Log(String.Format("{0}/{1}: callback fallo: {2}", s.Symbol, s.Timeframe, ex.Message))
                End Try
            Next
        End If

        s.ProximaConsulta = ProximaConsulta(velas, s.Granularidad, ahora)
        s.MercadoAbierto = s.ProximaConsulta <> ahora + SEGUNDOS_SIN_VELA_ABIERTA
    End Function

    ''' <summary>Estado de cada suscripcion (para la vigilancia y /api/heavy/salud).</summary>
    Public Function Estado() As JArray
        Dim res As New JArray()
        Dim ahora = AhoraEpoch.Invoke()
        SyncLock _lock
            For Each s In _subs.Values
                res.Add(New JObject From {
                    {"symbol", s.Symbol}, {"timeframe", s.Timeframe}, {"granularidad", s.Granularidad},
                    {"inicializada", s.Inicializada}, {"mercado_abierto", s.MercadoAbierto},
                    {"ultima_vela_epoch", s.UltimaEmitida},
                    {"minutos_desde_ultima_vela", If(s.UltimaEmitida > 0, Math.Round((ahora - s.UltimaEmitida - s.Granularidad) / 60.0, 1), -1.0)},
                    {"fallos_seguidos", s.FallosSeguidos},
                    {"minutos_desde_ultima_respuesta", If(s.UltimaRespuestaUtc > 0, Math.Round((ahora - s.UltimaRespuestaUtc) / 60.0, 1), -1.0)}
                })
            Next
        End SyncLock
        Return res
    End Function

    ''' <summary>
    ''' Velas cerradas (apertura + granularidad &lt;= ahora) con apertura posterior a
    ''' ultimaEmitida, en orden cronologico.
    ''' </summary>
    Friend Shared Function VelasCerradasNuevas(velas As JArray, granularidad As Integer, ahoraEpoch As Long,
                                               ultimaEmitida As Long) As List(Of CandleModel)
        Dim res As New List(Of CandleModel)()
        If velas Is Nothing Then Return res
        For Each t In velas
            Dim v = TryCast(t, JObject)
            If v Is Nothing OrElse v("epoch") Is Nothing Then Continue For
            Dim ep = CLng(v("epoch"))
            If ep + granularidad > ahoraEpoch OrElse ep <= ultimaEmitida Then Continue For
            res.Add(New CandleModel With {
                .Epoch = ep, .OpenPrice = CDec(v("open")), .High = CDec(v("high")),
                .Low = CDec(v("low")), .ClosePrice = CDec(v("close")), .Status = "[Cerrada]"
            })
        Next
        Return res.OrderBy(Function(c) c.Epoch).ToList()
    End Function

    ''' <summary>
    ''' Si hay una vela abierta, consultar justo despues de su cierre; si no (mercado
    ''' cerrado o datos atrasados), volver a mirar en un minuto.
    ''' </summary>
    Friend Shared Function ProximaConsulta(velas As JArray, granularidad As Integer, ahoraEpoch As Long) As Long
        Dim abierta As Long = -1
        If velas IsNot Nothing Then
            For Each t In velas
                Dim v = TryCast(t, JObject)
                If v Is Nothing OrElse v("epoch") Is Nothing Then Continue For
                Dim ep = CLng(v("epoch"))
                If ep + granularidad > ahoraEpoch Then abierta = Math.Max(abierta, ep)
            Next
        End If
        If abierta < 0 Then Return ahoraEpoch + SEGUNDOS_SIN_VELA_ABIERTA
        Return abierta + granularidad + SEGUNDOS_TRAS_CIERRE
    End Function

    Private Sub Log(msg As String)
        Try
            LogAction?.Invoke("[MultiStream] " & msg)
        Catch
        End Try
    End Sub

End Class

End Namespace
