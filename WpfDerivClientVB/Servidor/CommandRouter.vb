' ============================================================
' CommandRouter.vb
'
' Recibe instrucciones desde SignalRClientService.InstruccionRecibida
' y las traduce en llamadas a LiveStrategiesController.
'
' Tabla de comandos soportados:
'   estrategia.iniciar           -> IniciarAsync(payload) -> sesion_id
'   estrategia.detener           -> DetenerAsync(sesion_id)
'   estrategia.pausar            -> PausarReanudar(true)
'   estrategia.reanudar          -> PausarReanudar(false)
'   estrategia.actualizar-config -> ActualizarConfig(...)
'   estado.consultar             -> snapshot global
'   estado.motor                 -> snapshot de un motor especifico
'   ping                         -> pong
'   (otros)                      -> error
'
' Si la instruccion trae correlationId, responde via
' SignalRClientService.ResponderInstruccionAsync.
' ============================================================
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB.Servidor

    Public Module CommandRouter

        ''' <summary>Callback opcional para log textual (UI / archivo).</summary>
        Public Property LogAction As Action(Of String) = Nothing

        Private Sub Log(msg As String)
            Try
                If LogAction IsNot Nothing Then LogAction.Invoke(msg)
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Punto de entrada. Suscribir desde Application_Startup:
        '''   AddHandler SignalRClientService.GetInstance().InstruccionRecibida, AddressOf CommandRouter.Handle
        ''' </summary>
        Public Async Sub Handle(clienteId As String, instruccion As String, parametros As String,
                                correlationId As String, timestamp As String)
            Dim resp As JObject
            Try
                resp = Await Procesar(instruccion, parametros)
            Catch ex As Exception
                resp = New JObject From {{"ok", False}, {"error", ex.Message}, {"instruccion", instruccion}}
                Log("[CmdRouter] EXC en " & instruccion & ": " & ex.Message)
            End Try

            If Not String.IsNullOrEmpty(correlationId) Then
                Try
                    Await SignalRClientService.GetInstance().ResponderInstruccionAsync(
                        correlationId, resp.ToString(Formatting.None))
                Catch ex As Exception
                    Log("[CmdRouter] No pude responder corrId=" & correlationId & ": " & ex.Message)
                End Try
            End If
        End Sub

        Private Async Function Procesar(instruccion As String, parametros As String) As Task(Of JObject)
            Dim payload = ParsePayload(parametros)
            Dim ctrl = LiveStrategiesController.GetInstance()

            Select Case (If(instruccion, "")).ToLowerInvariant()

                Case "ping"
                    Return New JObject From {
                        {"ok", True}, {"pong", True},
                        {"timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}
                    }

                Case "estrategia.iniciar"
                    Dim nombre = StrFromPayload(payload, "estrategia")
                    Dim symbol = StrFromPayload(payload, "symbol")
                    Dim tf = StrFromPayload(payload, "timeframe")
                    Dim conf = IntFromPayload(payload, "confianza", 55)
                    Dim payout = DecFromPayload(payload, "payout", 85D)
                    Dim dur = IntFromPayload(payload, "duracion", 5)
                    Dim monto = DecFromPayload(payload, "monto", 1D)
                    Dim ventana = IntFromPayload(payload, "ventana_indicadores", 100)
                    Dim horas = IntFromPayload(payload, "horas_operacion", 0)
                    Dim limGan = DecFromPayload(payload, "limite_ganancia", 0D)
                    Dim limPer = DecFromPayload(payload, "limite_perdida", 0D)

                    If String.IsNullOrEmpty(nombre) OrElse String.IsNullOrEmpty(symbol) OrElse String.IsNullOrEmpty(tf) Then
                        Return New JObject From {
                            {"ok", False},
                            {"error", "estrategia, symbol y timeframe son obligatorios"}
                        }
                    End If

                    Dim sid = Await ctrl.IniciarAsync(nombre, symbol, tf, conf, payout, dur, monto,
                                                      ventana, horas, limGan, limPer)
                    Return New JObject From {
                        {"ok", True}, {"sesion_id", sid},
                        {"estrategia", nombre}, {"symbol", symbol}, {"timeframe", tf}
                    }

                Case "estrategia.detener"
                    Dim sid = IntFromPayload(payload, "sesion_id", 0)
                    If sid <= 0 Then Return New JObject From {{"ok", False}, {"error", "sesion_id requerido"}}
                    Dim ok = Await ctrl.DetenerAsync(sid)
                    Return New JObject From {{"ok", ok}, {"sesion_id", sid}}

                Case "estrategia.pausar"
                    Dim sid = IntFromPayload(payload, "sesion_id", 0)
                    If sid <= 0 Then Return New JObject From {{"ok", False}, {"error", "sesion_id requerido"}}
                    Return New JObject From {{"ok", ctrl.PausarReanudar(sid, True)}, {"sesion_id", sid}, {"pausado", True}}

                Case "estrategia.reanudar"
                    Dim sid = IntFromPayload(payload, "sesion_id", 0)
                    If sid <= 0 Then Return New JObject From {{"ok", False}, {"error", "sesion_id requerido"}}
                    Return New JObject From {{"ok", ctrl.PausarReanudar(sid, False)}, {"sesion_id", sid}, {"pausado", False}}

                Case "estrategia.actualizar-config"
                    Dim sid = IntFromPayload(payload, "sesion_id", 0)
                    If sid <= 0 Then Return New JObject From {{"ok", False}, {"error", "sesion_id requerido"}}
                    Dim monto = DecFromPayload(payload, "monto", -1D)
                    Dim conf = IntFromPayload(payload, "confianza", -1)
                    Dim dur = IntFromPayload(payload, "duracion", -1)
                    Dim horas = IntFromPayload(payload, "horas_operacion", -1)
                    Dim limGan = DecFromPayload(payload, "limite_ganancia", -1D)
                    Dim limPer = DecFromPayload(payload, "limite_perdida", -1D)
                    Dim ok = ctrl.ActualizarConfig(sid, monto, conf, dur, horas, limGan, limPer)
                    Return New JObject From {{"ok", ok}, {"sesion_id", sid}}

                Case "estado.consultar"
                    Return ctrl.GetEstadoGlobal()

                Case "estado.motor"
                    Dim sid = IntFromPayload(payload, "sesion_id", 0)
                    If sid <= 0 Then Return New JObject From {{"ok", False}, {"error", "sesion_id requerido"}}
                    Return ctrl.GetEstadoMotor(sid)

                Case "reanudar.activas"
                    Dim n = Await ctrl.ReanudarActivasAsync()
                    Return New JObject From {{"ok", True}, {"reanudadas", n}}

                Case Else
                    Return New JObject From {
                        {"ok", False},
                        {"error", "Instruccion no reconocida: " & instruccion}
                    }
            End Select
        End Function

        Private Function ParsePayload(parametros As String) As JObject
            If String.IsNullOrWhiteSpace(parametros) Then Return New JObject()
            Try
                Dim t = JToken.Parse(parametros)
                If TypeOf t Is JObject Then Return CType(t, JObject)
            Catch
            End Try
            Return New JObject()
        End Function

        Private Function StrFromPayload(p As JObject, key As String, Optional def As String = "") As String
            If p Is Nothing OrElse p(key) Is Nothing Then Return def
            Return p(key).ToString()
        End Function

        Private Function IntFromPayload(p As JObject, key As String, def As Integer) As Integer
            If p Is Nothing OrElse p(key) Is Nothing Then Return def
            Dim v As Integer
            If Integer.TryParse(p(key).ToString(), v) Then Return v
            Return def
        End Function

        Private Function DecFromPayload(p As JObject, key As String, def As Decimal) As Decimal
            If p Is Nothing OrElse p(key) Is Nothing Then Return def
            Dim v As Decimal
            If Decimal.TryParse(p(key).ToString().Replace(",", "."),
                                 Globalization.NumberStyles.Any,
                                 Globalization.CultureInfo.InvariantCulture, v) Then
                Return v
            End If
            Return def
        End Function

    End Module

End Namespace
