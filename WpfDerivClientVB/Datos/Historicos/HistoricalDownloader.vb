Imports System.Collections.Generic
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    Public Class DownloadProgress
        Public Property PageNum As Integer
        Public Property TotalSaved As Integer
        Public Property OldestDate As String
        Public Property NewestDate As String
        Public Property StatusMsg As String
        Public Property IsComplete As Boolean
        Public Property HasError As Boolean
    End Class

    Public Class HistoricalDownloader

        Private ReadOnly API_URI As String = "wss://ws.derivws.com/websockets/v3?app_id=1089"
        Private ReadOnly _repo As CandleRepository
        Private _cts As CancellationTokenSource

        Private ReadOnly GranularityMap As New Dictionary(Of String, Integer) From {
            {"1m", 60}, {"5m", 300}, {"15m", 900}, {"1h", 3600}, {"4h", 14400}, {"1d", 86400}
        }

        Public Sub New(repo As CandleRepository)
            _repo = repo
        End Sub

        Public Sub StopDownload()
            If _cts IsNot Nothing Then _cts.Cancel()
        End Sub

        Public Async Function DownloadAsync(platform As String,
                                             symbol As String,
                                             timeframe As String,
                                             fromDate As DateTime,
                                             toDate As DateTime,
                                             onProgress As Action(Of DownloadProgress)) As Task

            _cts = New CancellationTokenSource()
            Dim token As CancellationToken = _cts.Token

            Dim granularity As Integer = GranularityMap(timeframe)
            Dim totalSaved As Integer = 0
            Dim pageNum As Integer = 0
            Dim currentEndEpoch As Long = ToEpoch(toDate.AddHours(23).AddMinutes(59))
            Dim startEpoch As Long = ToEpoch(fromDate)

            Dim ws As New ClientWebSocket()
            Try
                Await ws.ConnectAsync(New Uri(API_URI), token)

                Do While Not token.IsCancellationRequested

                    pageNum += 1

                    ' Construir request JSON sin la palabra reservada end como propiedad
                    Dim requestJson As New JObject()
                    requestJson("ticks_history") = symbol
                    requestJson("adjust_start_time") = 1
                    requestJson("count") = 1000
                    requestJson("style") = "candles"
                    requestJson("end") = currentEndEpoch
                    requestJson("granularity") = granularity
                    requestJson("start") = startEpoch

                    Dim reqBytes As Byte() = Encoding.UTF8.GetBytes(requestJson.ToString())
                    Await ws.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, token)

                    Dim data As JObject = Await ReceiveMessageAsync(ws, token)

                    If data Is Nothing Then
                        onProgress(New DownloadProgress With {
                            .StatusMsg = "Conexion cerrada por el servidor.",
                            .IsComplete = True
                        })
                        Exit Do
                    End If

                    If data.ContainsKey("error") Then
                        Dim errMsg As String = data("error")("message").ToString()
                        onProgress(New DownloadProgress With {
                            .StatusMsg = "Error API: " & errMsg,
                            .HasError = True,
                            .IsComplete = True
                        })
                        Exit Do
                    End If

                    If Not data.ContainsKey("candles") Then
                        onProgress(New DownloadProgress With {
                            .StatusMsg = "Respuesta inesperada del servidor.",
                            .IsComplete = True
                        })
                        Exit Do
                    End If

                    Dim arr As JArray = CType(data("candles"), JArray)

                    If arr.Count = 0 Then
                        onProgress(New DownloadProgress With {
                            .PageNum = pageNum,
                            .TotalSaved = totalSaved,
                            .StatusMsg = String.Format("Descarga completada. No hay mas datos. Total: {0} velas en {1} paginas.", totalSaved, pageNum),
                            .IsComplete = True
                        })
                        Exit Do
                    End If

                    ' Convertir a CandleModel
                    Dim candleList As New List(Of CandleModel)()
                    Dim idx As Integer = 0
                    Do While idx < arr.Count
                        Dim item As JObject = CType(arr(idx), JObject)
                        Dim m As New CandleModel()
                        m.Epoch = CLng(item("epoch"))
                        m.OpenTime = EpochToLocal(m.Epoch)
                        m.OpenPrice = CDec(item("open"))
                        m.High = CDec(item("high"))
                        m.Low = CDec(item("low"))
                        m.ClosePrice = CDec(item("close"))
                        m.Status = "[Cerrada]"
                        candleList.Add(m)
                        idx += 1
                    Loop

                    ' Guardar en BD (await aqui porque ya estamos en thread pool)
                    Await _repo.SaveBatchAsync(platform, symbol, timeframe, candleList, isClosed:=True)
                    totalSaved += candleList.Count

                    Dim oldestEpoch As Long = CLng(CType(arr(0), JObject)("epoch"))
                    Dim newestEpoch As Long = CLng(CType(arr(arr.Count - 1), JObject)("epoch"))

                    onProgress(New DownloadProgress With {
                        .PageNum = pageNum,
                        .TotalSaved = totalSaved,
                        .OldestDate = EpochToLocal(oldestEpoch),
                        .NewestDate = EpochToLocal(newestEpoch),
                        .StatusMsg = String.Format("Pagina {0}: +{1} velas | Total: {2} | Desde: {3}",
                                                    pageNum, candleList.Count, totalSaved, EpochToLocal(oldestEpoch)),
                        .IsComplete = False
                    })

                    ' Si recibimos menos de 1000 o ya llegamos al inicio: terminar
                    If arr.Count < 1000 OrElse oldestEpoch <= startEpoch Then
                        onProgress(New DownloadProgress With {
                            .PageNum = pageNum,
                            .TotalSaved = totalSaved,
                            .OldestDate = EpochToLocal(oldestEpoch),
                            .StatusMsg = String.Format("Descarga completada exitosamente. {0} velas en {1} paginas.", totalSaved, pageNum),
                            .IsComplete = True
                        })
                        Exit Do
                    End If

                    ' Mover el cursor hacia atras para la siguiente pagina
                    currentEndEpoch = oldestEpoch - 1

                    ' Pausa para no saturar el API
                    Await Task.Delay(400, token)
                Loop

            Catch ex As OperationCanceledException
                onProgress(New DownloadProgress With {
                    .PageNum = pageNum,
                    .TotalSaved = totalSaved,
                    .StatusMsg = String.Format("Descarga detenida por el usuario. Total guardado: {0} velas.", totalSaved),
                    .IsComplete = True
                })
            Catch ex As Exception
                onProgress(New DownloadProgress With {
                    .StatusMsg = "Error inesperado: " & ex.Message,
                    .HasError = True,
                    .IsComplete = True
                })
            Finally
                Try
                    If ws.State = WebSocketState.Open Then
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fin descarga", CancellationToken.None).Wait(2000)
                    End If
                Catch
                End Try
                ws.Dispose()
            End Try
        End Function

        Private Async Function ReceiveMessageAsync(ws As ClientWebSocket, token As CancellationToken) As Task(Of JObject)
            Dim buffer(65535) As Byte
            Dim sb As New StringBuilder()
            Dim result As WebSocketReceiveResult
            Do
                result = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), token)
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
            Loop While Not result.EndOfMessage

            If result.MessageType = WebSocketMessageType.Close Then Return Nothing
            Return JObject.Parse(sb.ToString())
        End Function

        Private Shared Function ToEpoch(dt As DateTime) As Long
            Dim base As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Return CLng((dt.ToUniversalTime() - base).TotalSeconds)
        End Function

        Private Shared Function EpochToLocal(epoch As Long) As String
            Dim base As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            Return base.AddSeconds(epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        End Function
    End Class
End Namespace
