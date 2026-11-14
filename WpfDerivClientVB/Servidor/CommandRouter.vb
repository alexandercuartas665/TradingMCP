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
                ' Seguridad: verificar firma antes de ejecutar cualquier instruccion que modifique estado
                Dim payloadVerificado As String = Nothing
                Dim rechazo = VerificarInstruccion(clienteId, instruccion, parametros, payloadVerificado)
                If rechazo <> "" Then
                    Log("[CmdRouter] RECHAZADA '" & instruccion & "' de " & clienteId & ": " & rechazo)
                    AppLogger.Warn("[CmdRouter] Instruccion rechazada '" & instruccion & "' (" & clienteId & "): " & rechazo)
                    resp = New JObject From {{"ok", False}, {"error", rechazo}, {"instruccion", instruccion}}
                Else
                    resp = Await Procesar(instruccion, payloadVerificado)
                End If
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

                Case "riesgo.estado"
                    Return RiskManager.GetEstado()

                Case "riesgo.parar"
                    ' Detener todo remotamente esta permitido (firmado). Reanudar NO:
                    ' solo en local (API 8766 o UI), para que un servidor comprometido
                    ' no pueda levantar una parada de emergencia.
                    RiskManager.ActivarParada("Remota (SignalR): " & StrFromPayload(payload, "motivo", "sin motivo"))
                    Return RiskManager.GetEstado()

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

        ' ── Seguridad: firma HMAC de instrucciones ───────────────────────
        '
        ' Formato firmado de "parametros":
        '   {"payload":"<json como string>","ts":<unix segundos>,"nonce":"<unico>","firma":"<hex>"}
        ' firma = HMAC-SHA256(SignalRSecret,
        '           instruccion_en_minusculas + "\n" + ts + "\n" + nonce + "\n" + payload)  en hex minuscula
        '
        ' - Solo lectura (ping, estado.*): se aceptan sin firma.
        ' - El resto exige firma valida, ts dentro de +-120 s y nonce no repetido.
        ' - Broadcasts ("ALL") nunca pueden ejecutar instrucciones que modifican estado.

        Private ReadOnly SoloLectura As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "ping", "estado.consultar", "estado.motor", "riesgo.estado"
        }
        Private Const VentanaSegundos As Integer = 120
        Private ReadOnly _noncesVistos As New Dictionary(Of String, DateTime)()
        Private ReadOnly _noncesLock As New Object()

        ''' <summary>
        ''' Devuelve "" si la instruccion puede ejecutarse (y deja en payloadOut el JSON
        ''' de parametros a usar) o el motivo del rechazo.
        ''' </summary>
        Private Function VerificarInstruccion(clienteId As String, instruccion As String, parametros As String,
                                              ByRef payloadOut As String) As String
            Dim instr = If(instruccion, "").Trim().ToLowerInvariant()
            Dim envoltura = ParsePayload(parametros)
            Dim firmado = envoltura("firma") IsNot Nothing AndAlso envoltura("payload") IsNot Nothing

            If SoloLectura.Contains(instr) Then
                payloadOut = If(firmado, envoltura("payload").ToString(), parametros)
                Return ""
            End If

            If String.Equals(clienteId, "ALL", StringComparison.OrdinalIgnoreCase) Then
                Return "Las instrucciones que modifican estado no se aceptan por broadcast."
            End If

            Dim secreto = ConfigManager.LoadDbConfig().SignalRSecret
            If String.IsNullOrWhiteSpace(secreto) Then
                Return "SignalRSecret no configurado en db_config.json: solo se aceptan ping y estado.*."
            End If
            If Not firmado Then Return "Instruccion sin firma."

            Dim payload = envoltura("payload").ToString()
            Dim nonce = If(envoltura("nonce")?.ToString(), "")
            Dim tsTexto = If(envoltura("ts")?.ToString(), "")
            Dim firma = envoltura("firma").ToString().Trim().ToLowerInvariant()

            Dim ts As Long
            If Not Long.TryParse(tsTexto, ts) Then Return "ts invalido."
            Dim ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            If Math.Abs(ahora - ts) > VentanaSegundos Then Return "ts fuera de ventana (reloj desfasado o mensaje reenviado)."
            If nonce.Length < 8 Then Return "nonce invalido."

            Dim esperada = CalcularFirma(secreto, instr, tsTexto, nonce, payload)
            If Not AgentAuth.IgualTiempoConstante(firma, esperada) Then Return "Firma invalida."

            SyncLock _noncesLock
                Dim limite = DateTime.UtcNow
                For Each k In _noncesVistos.Where(Function(kv) kv.Value < limite).Select(Function(kv) kv.Key).ToList()
                    _noncesVistos.Remove(k)
                Next
                If _noncesVistos.ContainsKey(nonce) Then Return "nonce repetido (posible reenvio)."
                _noncesVistos(nonce) = DateTime.UtcNow.AddSeconds(VentanaSegundos * 2)
            End SyncLock

            payloadOut = payload
            Return ""
        End Function

        ''' <summary>
        ''' Envuelve y firma los parametros de una instruccion SALIENTE con SignalRSecret.
        ''' - Instrucciones de solo lectura, sin secreto configurado o ya firmadas: se devuelven tal cual.
        ''' Lo usa SignalRClientService.EnviarInstruccionAsync para que las ordenes que la
        ''' propia app envia (p.ej. "Enviar a Servidor Live") pasen la verificacion.
        ''' </summary>
        Public Function FirmarSaliente(instruccion As String, parametrosJson As String) As String
            Dim instr = If(instruccion, "").Trim().ToLowerInvariant()
            If SoloLectura.Contains(instr) Then Return parametrosJson
            Dim secreto = ConfigManager.LoadDbConfig().SignalRSecret
            If String.IsNullOrWhiteSpace(secreto) Then Return parametrosJson
            Dim actual = ParsePayload(parametrosJson)
            If actual("firma") IsNot Nothing AndAlso actual("payload") IsNot Nothing Then Return parametrosJson

            Dim payload = If(String.IsNullOrWhiteSpace(parametrosJson), "{}", parametrosJson)
            Dim ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(Globalization.CultureInfo.InvariantCulture)
            Dim nonceBytes(11) As Byte
            Using rng As New System.Security.Cryptography.RNGCryptoServiceProvider()
                rng.GetBytes(nonceBytes)
            End Using
            Dim nonce = String.Concat(nonceBytes.Select(Function(b) b.ToString("x2")))
            Dim sobre As New JObject From {
                {"payload", payload},
                {"ts", Long.Parse(ts, Globalization.CultureInfo.InvariantCulture)},
                {"nonce", nonce},
                {"firma", CalcularFirma(secreto, instr, ts, nonce, payload)}
            }
            Return sobre.ToString(Formatting.None)
        End Function

        ''' <summary>HMAC-SHA256 en hex minuscula. Publica para poder probarla.</summary>
        Public Function CalcularFirma(secreto As String, instruccion As String, ts As String,
                                      nonce As String, payload As String) As String
            Dim mensaje = instruccion.ToLowerInvariant() & vbLf & ts & vbLf & nonce & vbLf & payload
            Using h As New System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secreto))
                Dim hash = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(mensaje))
                Return String.Concat(hash.Select(Function(b) b.ToString("x2")))
            End Using
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
