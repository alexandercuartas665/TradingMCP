' ============================================================
' NotificacionService.vb
'
' Servicio para enviar notificaciones por mensajeria (WhatsApp via
' Evolution API u otro endpoint compatible).
'
' Origenes de datos:
'   - Telefono: ClientDerivCredentials (TelefonoReal o TelefonoVirtual)
'   - Endpoint y ApiKey: PostgresConfig (db_config.json)
'
' Uso basico:
'   Dim svc As New NotificacionService()
'   Dim r = Await svc.EnviarMensajeClienteAsync(clientId:=5,
'                       mensaje:="Estrategia A inicio sesion live.")
'   If Not r.Exito Then Log("Fallo: " & r.Mensaje)
'
' Equivalente en curl al payload que se construye:
'   curl --location --request POST '<Endpoint>' \
'     --header 'Content-Type: application/json' \
'     --header 'apikey: <ApiKey>' \
'     --data '{ "number": "573217626882", "text": "...", "linkPreview": false }'
' ============================================================
Imports System.Net.Http
Imports System.Text
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq

Namespace WpfDerivClientVB

    ''' <summary>Resultado de una operacion de envio de notificacion.</summary>
    Public Class ResultadoNotificacion
        Public Property Exito As Boolean
        ''' <summary>Mensaje legible para log o UI ("Mensaje enviado", "HTTP 401: ...", etc).</summary>
        Public Property Mensaje As String = ""
        ''' <summary>Body crudo de la respuesta HTTP (util para debug).</summary>
        Public Property Respuesta As String = ""
    End Class

    ''' <summary>
    ''' Servicio de notificaciones de mensajeria. Lee endpoint y apikey de la config
    ''' global (db_config.json) y telefono de las credenciales del cliente.
    ''' Thread-safe (cada llamada crea su propio HttpClient).
    ''' </summary>
    Public Class NotificacionService

        Private ReadOnly _repo As ClientRepository

        Public Sub New()
            _repo = New ClientRepository()
        End Sub

        ''' <summary>
        ''' Envia un mensaje al cliente identificado por clientId. El telefono se toma
        ''' de las credenciales registradas (DerivCredentials.TelefonoReal/Virtual).
        ''' </summary>
        ''' <param name="clientId">ID del cliente en la tabla clients.</param>
        ''' <param name="mensaje">Texto a enviar.</param>
        ''' <param name="preferirDemo">
        ''' True (default): usa primero TelefonoVirtual, si esta vacio cae a TelefonoReal.
        ''' False: usa primero TelefonoReal, si esta vacio cae a TelefonoVirtual.
        ''' </param>
        Public Async Function EnviarMensajeClienteAsync(clientId As Integer,
                                                         mensaje As String,
                                                         Optional preferirDemo As Boolean = True) _
                As Task(Of ResultadoNotificacion)
            If clientId <= 0 Then
                Return New ResultadoNotificacion With {.Exito = False, .Mensaje = "clientId invalido."}
            End If
            If String.IsNullOrWhiteSpace(mensaje) Then
                Return New ResultadoNotificacion With {.Exito = False, .Mensaje = "Mensaje vacio."}
            End If

            Dim creds As ClientDerivCredentials = Nothing
            Try
                creds = Await _repo.GetDerivCredentialsAsync(clientId)
            Catch ex As Exception
                Return New ResultadoNotificacion With {.Exito = False, .Mensaje = "Error BD: " & ex.Message}
            End Try
            If creds Is Nothing Then
                Return New ResultadoNotificacion With {.Exito = False,
                                                       .Mensaje = "Cliente sin credenciales registradas."}
            End If

            Dim phone As String = If(preferirDemo, creds.TelefonoVirtual, creds.TelefonoReal)
            If String.IsNullOrWhiteSpace(phone) Then
                phone = If(preferirDemo, creds.TelefonoReal, creds.TelefonoVirtual)
            End If
            If String.IsNullOrWhiteSpace(phone) Then
                Return New ResultadoNotificacion With {.Exito = False,
                                                       .Mensaje = "Cliente sin telefono configurado en credenciales."}
            End If
            Return Await EnviarMensajeATelefonoAsync(phone, mensaje)
        End Function

        ''' <summary>
        ''' Envia un mensaje a un numero especifico. Util para casos en que el numero
        ''' viene de otra fuente o se quiere notificar a alguien que no esta en BD.
        ''' </summary>
        ''' <param name="numero">Numero con codigo de pais (ej. "573217626882" o "+57 321 762 6882").</param>
        Public Async Function EnviarMensajeATelefonoAsync(numero As String, mensaje As String) _
                As Task(Of ResultadoNotificacion)
            If String.IsNullOrWhiteSpace(numero) Then
                Return New ResultadoNotificacion With {.Exito = False, .Mensaje = "Numero vacio."}
            End If
            If String.IsNullOrWhiteSpace(mensaje) Then
                Return New ResultadoNotificacion With {.Exito = False, .Mensaje = "Mensaje vacio."}
            End If

            Dim cfg = ConfigManager.LoadDbConfig()
            If cfg Is Nothing OrElse String.IsNullOrWhiteSpace(cfg.Endpoint) Then
                Return New ResultadoNotificacion With {.Exito = False,
                                                       .Mensaje = "Endpoint no configurado en Settings."}
            End If
            If String.IsNullOrWhiteSpace(cfg.ApiKey) Then
                Return New ResultadoNotificacion With {.Exito = False,
                                                       .Mensaje = "ApiKey no configurada en Settings."}
            End If

            Dim numeroLimpio = LimpiarNumero(numero)

            Try
                Using handler As New HttpClientHandler()
                    Using client As New HttpClient(handler)
                        client.Timeout = TimeSpan.FromSeconds(20)
                        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", cfg.ApiKey)
                        Dim body As New JObject From {
                            {"number", numeroLimpio},
                            {"text", mensaje},
                            {"linkPreview", False}
                        }
                        Using content As New StringContent(body.ToString(),
                                                           Encoding.UTF8, "application/json")
                            Using resp = Await client.PostAsync(cfg.Endpoint, content)
                                Dim respBody = Await resp.Content.ReadAsStringAsync()
                                If resp.IsSuccessStatusCode Then
                                    Return New ResultadoNotificacion With {
                                        .Exito = True,
                                        .Mensaje = String.Format("Mensaje enviado a {0}.", numeroLimpio),
                                        .Respuesta = respBody
                                    }
                                Else
                                    Return New ResultadoNotificacion With {
                                        .Exito = False,
                                        .Mensaje = String.Format("HTTP {0}: {1}", CInt(resp.StatusCode), respBody),
                                        .Respuesta = respBody
                                    }
                                End If
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Return New ResultadoNotificacion With {.Exito = False,
                                                       .Mensaje = "Error de red: " & ex.Message}
            End Try
        End Function

        ''' <summary>
        ''' Helper de prueba: verifica que endpoint+apikey respondan correctamente
        ''' enviando un mensaje de prueba al numero indicado.
        ''' </summary>
        Public Async Function ProbarConexionAsync(numeroDestino As String) As Task(Of ResultadoNotificacion)
            Return Await EnviarMensajeATelefonoAsync(numeroDestino,
                "Prueba de conexion desde el sistema. Si recibes este mensaje, el servicio funciona correctamente.")
        End Function

        ''' <summary>Quita "+" espacios y guiones — WhatsApp espera solo digitos.</summary>
        Private Shared Function LimpiarNumero(p As String) As String
            If String.IsNullOrEmpty(p) Then Return ""
            Return p.Replace("+", "") _
                    .Replace(" ", "") _
                    .Replace("-", "") _
                    .Replace("(", "") _
                    .Replace(")", "") _
                    .Trim()
        End Function

    End Class

End Namespace
