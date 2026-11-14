' ============================================================
' BackgroundStreamingSubscriber.vb
'
' Mantiene N WebSockets independientes a Deriv, uno por cada
' (symbol, timeframe) requerido por las estrategias live activas.
' Permite que varias estrategias operen simultaneamente sobre
' activos distintos sin que el chart visible cambie.
'
' Uso desde MainWindow:
'   _multiSub = New BackgroundStreamingSubscriber()
'   _multiSub.OnCandleCerrada = Sub(c, sym, tf) RaiseEvent OnCandleCerrada(c, sym, tf)
'   Await _multiSub.SubscribeAsync("R_100", "1h", 3600)
'   ' ... velas llegaran via callback ...
'   Await _multiSub.UnsubscribeAsync("R_100", "1h")
' ============================================================
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

Public Class BackgroundStreamingSubscriber

    Private Const API_URI As String = "wss://ws.derivws.com/websockets/v3?app_id=1089"

    ''' <summary>Subscripciones activas, indexadas por "symbol/timeframe".</summary>
    Private ReadOnly _subs As New Dictionary(Of String, BackgroundSub)()
    Private ReadOnly _lock As New Object()

    ''' <summary>Callback invocado cada vez que cierra una vela en cualquier subscription.</summary>
    Public Property OnCandleCerrada As Action(Of CandleModel, String, String)

    ''' <summary>Callback opcional para log de eventos del subscriber.</summary>
    Public Property LogAction As Action(Of String)

    Private Shared Function MakeKey(symbol As String, timeframe As String) As String
        Return (symbol & "/" & timeframe).ToLowerInvariant()
    End Function

    ''' <summary>Lista de subscripciones activas en formato "symbol/tf".</summary>
    Public ReadOnly Property Activas As List(Of String)
        Get
            SyncLock _lock
                Return _subs.Keys.ToList()
            End SyncLock
        End Get
    End Property

    ''' <summary>True si ya hay una subscription para (symbol, timeframe).</summary>
    Public Function EstaSuscrito(symbol As String, timeframe As String) As Boolean
        SyncLock _lock
            Return _subs.ContainsKey(MakeKey(symbol, timeframe))
        End SyncLock
    End Function

    ''' <summary>
    ''' Abre un WebSocket nuevo para (symbol, timeframe). Si ya existe, no hace nada.
    ''' Devuelve True si la subscription quedo activa.
    ''' </summary>
    Public Async Function SubscribeAsync(symbol As String, timeframe As String, granularity As Integer) As Task(Of Boolean)
        If String.IsNullOrWhiteSpace(symbol) OrElse String.IsNullOrWhiteSpace(timeframe) Then Return False
        Dim key = MakeKey(symbol, timeframe)
        SyncLock _lock
            If _subs.ContainsKey(key) Then Return True
        End SyncLock

        Dim nuevoSub As New BackgroundSub(symbol, timeframe, granularity)
        nuevoSub.OnCandleCerrada = Sub(c, sym, tf) OnCandleCerrada?.Invoke(c, sym, tf)
        nuevoSub.LogAction = Sub(msg) LogAction?.Invoke(msg)

        Dim ok As Boolean = Await nuevoSub.StartAsync()
        If ok Then
            SyncLock _lock
                _subs(key) = nuevoSub
            End SyncLock
            Log(String.Format("Background WS abierto: {0}/{1}", symbol, timeframe))
        Else
            Log(String.Format("Background WS fallo al abrir: {0}/{1}", symbol, timeframe))
        End If
        Return ok
    End Function

    ''' <summary>Cierra el WebSocket asociado a (symbol, timeframe), si existe.</summary>
    Public Async Function UnsubscribeAsync(symbol As String, timeframe As String) As Task
        Dim key = MakeKey(symbol, timeframe)
        Dim subExistente As BackgroundSub = Nothing
        SyncLock _lock
            If _subs.TryGetValue(key, subExistente) Then
                _subs.Remove(key)
            End If
        End SyncLock
        If subExistente IsNot Nothing Then
            Await subExistente.StopAsync()
            Log(String.Format("Background WS cerrado: {0}/{1}", symbol, timeframe))
        End If
    End Function

    ''' <summary>Cierra TODOS los WebSockets background.</summary>
    Public Async Function UnsubscribeAllAsync() As Task
        Dim aCerrar As List(Of BackgroundSub)
        SyncLock _lock
            aCerrar = _subs.Values.ToList()
            _subs.Clear()
        End SyncLock
        For Each s In aCerrar
            Try
                Await s.StopAsync()
            Catch
            End Try
        Next
    End Function

    Private Sub Log(msg As String)
        Try
            LogAction?.Invoke("[MultiStream] " & msg)
        Catch
        End Try
    End Sub

    ' =================================================================
    '  Inner class: una subscription (un WebSocket dedicado)
    ' =================================================================
    Private Class BackgroundSub
        Private ReadOnly _symbol As String
        Private ReadOnly _timeframe As String
        Private ReadOnly _granularity As Integer

        Private _ws As ClientWebSocket
        Private _cts As CancellationTokenSource
        Private _currentOpenEpoch As Long = 0
        Private _lastClosedSnapshot As CandleModel = Nothing

        Public Property OnCandleCerrada As Action(Of CandleModel, String, String)
        Public Property LogAction As Action(Of String)

        Public Sub New(symbol As String, timeframe As String, granularity As Integer)
            _symbol = symbol
            _timeframe = timeframe
            _granularity = granularity
        End Sub

        Public Async Function StartAsync() As Task(Of Boolean)
            Try
                _cts = New CancellationTokenSource()
                _ws = New ClientWebSocket()
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
                Await _ws.ConnectAsync(New Uri(API_URI), _cts.Token)
                Dim req = BuildRequest(_symbol, _granularity)
                Dim bytes = Encoding.UTF8.GetBytes(req)
                Await _ws.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _cts.Token)
                ' Iniciar bucle de escucha en background
#Disable Warning BC42358
                Task.Run(Function() ListenLoop())
#Enable Warning BC42358
                Return True
            Catch ex As Exception
                LogAction?.Invoke(String.Format("[Sub {0}/{1}] Error conectando: {2}",
                                                _symbol, _timeframe, ex.Message))
                Return False
            End Try
        End Function

        Public Async Function StopAsync() As Task
            If _cts IsNot Nothing Then
                Try
                    _cts.Cancel()
                Catch
                End Try
            End If
            If _ws IsNot Nothing Then
                Try
                    If _ws.State = WebSocketState.Open Then
                        Await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stop", CancellationToken.None)
                    End If
                Catch
                End Try
                Try
                    _ws.Dispose()
                Catch
                End Try
                _ws = Nothing
            End If
        End Function

        Private Shared Function BuildRequest(symbol As String, granularity As Integer) As String
            Dim json As New JObject()
            json.Add("ticks_history", New JValue(symbol))
            json.Add("adjust_start_time", New JValue(1))
            json.Add("count", New JValue(2))   ' minimo: solo necesitamos arrancar el ciclo
            json.Add("style", New JValue("candles"))
            json.Add("end", New JValue("latest"))
            json.Add("granularity", New JValue(granularity))
            json.Add("subscribe", New JValue(1))
            Return json.ToString()
        End Function

        Private Async Function ListenLoop() As Task
            Dim buffer(16383) As Byte
            Dim intentos As Integer = 0
            Do While Not _cts.IsCancellationRequested
                Try
                    Do While _ws IsNot Nothing AndAlso _ws.State = WebSocketState.Open _
                             AndAlso Not _cts.IsCancellationRequested
                        Dim sb As New StringBuilder()
                        Dim result As WebSocketReceiveResult
                        Do
                            result = Await _ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), _cts.Token)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                        Loop While Not result.EndOfMessage

                        If result.MessageType = WebSocketMessageType.Close Then Exit Do

                        ProcesarMensaje(sb.ToString())
                    Loop
                Catch ex As OperationCanceledException
                    Exit Do
                Catch ex As Exception
                    LogAction?.Invoke(String.Format("[Sub {0}/{1}] Error recibiendo: {2}",
                                                    _symbol, _timeframe, ex.Message))
                End Try
                If _cts.IsCancellationRequested Then Exit Do

                ' Reconexion simple
                intentos += 1
                Dim espera As Integer = If(intentos <= 3, 3, 15)
                Try
                    Await Task.Delay(espera * 1000, _cts.Token)
                Catch
                    Exit Do
                End Try
                If _cts.IsCancellationRequested Then Exit Do
                Try
                    If _ws IsNot Nothing Then _ws.Dispose()
                    _ws = New ClientWebSocket()
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
                    Await _ws.ConnectAsync(New Uri(API_URI), _cts.Token)
                    Dim req = BuildRequest(_symbol, _granularity)
                    Dim bytes = Encoding.UTF8.GetBytes(req)
                    Await _ws.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _cts.Token)
                    intentos = 0
                    LogAction?.Invoke(String.Format("[Sub {0}/{1}] Reconectado.", _symbol, _timeframe))
                Catch ex As Exception
                    LogAction?.Invoke(String.Format("[Sub {0}/{1}] Reconexion fallo: {2}",
                                                    _symbol, _timeframe, ex.Message))
                End Try
            Loop
        End Function

        Private Sub ProcesarMensaje(json As String)
            Try
                Dim root = JObject.Parse(json)
                Dim msgType = root("msg_type")?.ToString()
                Select Case msgType
                    Case "candles"
                        ' Respuesta inicial: array de velas historicas. Tomamos la mas reciente
                        ' como referencia de la vela "abierta" en curso.
                        Dim arr = TryCast(root("candles"), JArray)
                        If arr IsNot Nothing AndAlso arr.Count > 0 Then
                            Dim last = TryCast(arr(arr.Count - 1), JObject)
                            If last IsNot Nothing Then
                                _currentOpenEpoch = CLng(last("epoch"))
                                _lastClosedSnapshot = New CandleModel With {
                                    .Epoch = CLng(last("epoch")),
                                    .OpenPrice = CDec(last("open")),
                                    .High = CDec(last("high")),
                                    .Low = CDec(last("low")),
                                    .ClosePrice = CDec(last("close"))
                                }
                            End If
                        End If

                    Case "ohlc"
                        Dim ohlc = TryCast(root("ohlc"), JObject)
                        If ohlc Is Nothing Then Return
                        Dim tickEpoch As Long = CLng(ohlc("open_time"))
                        Dim isNewCandle As Boolean = (_currentOpenEpoch <> 0 AndAlso tickEpoch <> _currentOpenEpoch)

                        If isNewCandle Then
                            ' La vela que estaba "abierta" se cerro. Disparar callback con su snapshot.
                            If _lastClosedSnapshot IsNot Nothing Then
                                Dim snap = _lastClosedSnapshot
                                snap.Status = "[Cerrada]"
                                Try
                                    OnCandleCerrada?.Invoke(snap, _symbol, _timeframe)
                                Catch ex As Exception
                                    LogAction?.Invoke(String.Format("[Sub {0}/{1}] Callback fallo: {2}",
                                                                    _symbol, _timeframe, ex.Message))
                                End Try
                            End If
                            ' Empezar a trackear la nueva vela
                            _currentOpenEpoch = tickEpoch
                            _lastClosedSnapshot = New CandleModel With {
                                .Epoch = tickEpoch,
                                .OpenPrice = CDec(ohlc("open")),
                                .High = CDec(ohlc("high")),
                                .Low = CDec(ohlc("low")),
                                .ClosePrice = CDec(ohlc("close"))
                            }
                        Else
                            ' Tick dentro de la vela actual: actualizar HLC del snapshot en curso
                            If _lastClosedSnapshot IsNot Nothing Then
                                _lastClosedSnapshot.High = CDec(ohlc("high"))
                                _lastClosedSnapshot.Low = CDec(ohlc("low"))
                                _lastClosedSnapshot.ClosePrice = CDec(ohlc("close"))
                            End If
                        End If
                End Select
            Catch ex As Exception
                LogAction?.Invoke(String.Format("[Sub {0}/{1}] Parse fallo: {2}",
                                                _symbol, _timeframe, ex.Message))
            End Try
        End Sub
    End Class

End Class

End Namespace
