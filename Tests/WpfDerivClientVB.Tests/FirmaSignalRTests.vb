Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

''' <summary>
''' Firma HMAC de instrucciones SignalR: el formato debe coincidir con el firmador
''' Python (C:\tmp\signalr_firma.py) y rechazar reenvios, alteraciones y broadcasts.
''' </summary>
<TestClass>
Public Class FirmaSignalRTests

    Private Const Secreto As String = "secreto-de-prueba-0123456789abcdef"

    Private Shared Function Verificar(cliente As String, instr As String, params As String, ByRef payload As String) As String
        Return CommandRouter.VerificarInstruccionConSecreto(cliente, instr, params, Secreto, payload)
    End Function

    Private Shared Function Firmar(instr As String, payload As String) As String
        Return CommandRouter.FirmarSalienteConSecreto(instr, payload, Secreto)
    End Function

    <TestMethod>
    Public Sub La_firma_es_HMAC_SHA256_del_formato_documentado()
        ' Mismo calculo que hace el firmador Python
        Dim msg = "estrategia.detener" & vbLf & "1700000000" & vbLf & "abcdef123456" & vbLf & "{""sesion_id"":1}"
        Dim esperado As String
        Using h As New HMACSHA256(Encoding.UTF8.GetBytes(Secreto))
            esperado = String.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(msg)).Select(Function(b) b.ToString("x2")))
        End Using
        Assert.AreEqual(esperado, CommandRouter.CalcularFirma(Secreto, "Estrategia.Detener", "1700000000", "abcdef123456", "{""sesion_id"":1}"))
    End Sub

    <TestMethod>
    Public Sub Orden_firmada_por_la_app_es_aceptada_y_devuelve_el_payload()
        Dim payload = "{""estrategia"":""X"",""symbol"":""R_75"",""timeframe"":""1h""}"
        Dim recuperado As String = Nothing
        Assert.AreEqual("", Verificar("BITCODEV1", "estrategia.iniciar", Firmar("estrategia.iniciar", payload), recuperado))
        Assert.AreEqual(payload, recuperado)
    End Sub

    <TestMethod>
    Public Sub Solo_lectura_se_acepta_sin_firma_y_no_se_envuelve()
        Dim p As String = Nothing
        Assert.AreEqual("", Verificar("BITCODEV1", "ping", "{}", p))
        Assert.AreEqual("", Verificar("BITCODEV1", "estado.consultar", "{}", p))
        Assert.AreEqual("", Verificar("BITCODEV1", "riesgo.estado", "{}", p))
        Assert.AreEqual("{}", Firmar("ping", "{}"))
    End Sub

    <TestMethod>
    Public Sub Orden_sin_firma_es_rechazada()
        Dim p As String = Nothing
        StringAssert.Contains(Verificar("BITCODEV1", "estrategia.detener", "{""sesion_id"":1}", p), "sin firma")
    End Sub

    <TestMethod>
    Public Sub Mensaje_reenviado_es_rechazado()
        Dim sobre = Firmar("estrategia.detener", "{""sesion_id"":1}")
        Dim p As String = Nothing
        Assert.AreEqual("", Verificar("BITCODEV1", "estrategia.detener", sobre, p))
        StringAssert.Contains(Verificar("BITCODEV1", "estrategia.detener", sobre, p), "nonce repetido")
    End Sub

    <TestMethod>
    Public Sub Payload_alterado_es_rechazado()
        Dim j = JObject.Parse(Firmar("estrategia.detener", "{""sesion_id"":1}"))
        j("payload") = "{""sesion_id"":1,""monto"":500}"
        Dim p As String = Nothing
        StringAssert.Contains(Verificar("BITCODEV1", "estrategia.detener", j.ToString(), p), "Firma invalida")
    End Sub

    <TestMethod>
    Public Sub Firma_de_otra_instruccion_es_rechazada()
        Dim p As String = Nothing
        StringAssert.Contains(Verificar("BITCODEV1", "estrategia.iniciar", Firmar("estrategia.detener", "{}"), p), "Firma invalida")
    End Sub

    <TestMethod>
    Public Sub Mensaje_viejo_es_rechazado()
        Dim j = JObject.Parse(Firmar("estrategia.detener", "{""sesion_id"":1}"))
        j("ts") = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()
        Dim p As String = Nothing
        StringAssert.Contains(Verificar("BITCODEV1", "estrategia.detener", j.ToString(), p), "fuera de ventana")
    End Sub

    <TestMethod>
    Public Sub Broadcast_no_puede_dar_ordenes_aunque_este_firmado()
        Dim p As String = Nothing
        StringAssert.Contains(Verificar("ALL", "estrategia.detener", Firmar("estrategia.detener", "{}"), p), "broadcast")
    End Sub

    <TestMethod>
    Public Sub Sin_secreto_configurado_solo_se_acepta_lectura()
        Dim p As String = Nothing
        StringAssert.Contains(CommandRouter.VerificarInstruccionConSecreto("BITCODEV1", "estrategia.detener", "{}", "", p), "no configurado")
        Assert.AreEqual("", CommandRouter.VerificarInstruccionConSecreto("BITCODEV1", "ping", "{}", "", p))
    End Sub

End Class
