Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

''' <summary>Velas en vivo por la conexion autenticada y reglas del modo forward.</summary>
<TestClass>
Public Class StreamingForwardTests

    Private Const H As Integer = 3600

    Private Shared Function Vela(epoch As Long) As JObject
        Return New JObject From {{"epoch", epoch}, {"open", 1.1}, {"high", 1.2}, {"low", 1.0}, {"close", 1.15}}
    End Function

    Private Shared Function Velas(ParamArray epochs As Long()) As JArray
        Return New JArray(epochs.Select(Function(e) Vela(e)))
    End Function

    ' ---------- BackgroundStreamingSubscriber ----------

    <TestMethod>
    Public Sub Solo_emite_velas_cerradas_y_no_repite()
        Dim ahora As Long = 10 * H + 5            ' la vela de 10h esta abierta
        Dim r = BackgroundStreamingSubscriber.VelasCerradasNuevas(Velas(7 * H, 8 * H, 9 * H, 10 * H), H, ahora, 8 * H)
        Assert.AreEqual(1, r.Count)
        Assert.AreEqual(CLng(9 * H), r(0).Epoch)
        Assert.AreEqual(1.15D, r(0).ClosePrice)
    End Sub

    <TestMethod>
    Public Sub Tras_una_desconexion_emite_las_perdidas_en_orden()
        Dim r = BackgroundStreamingSubscriber.VelasCerradasNuevas(Velas(9 * H, 7 * H, 8 * H, 10 * H), H, 10 * H + 5, 6 * H)
        CollectionAssert.AreEqual({CLng(7 * H), CLng(8 * H), CLng(9 * H)}, r.Select(Function(c) c.Epoch).ToArray())
    End Sub

    <TestMethod>
    Public Sub Consulta_justo_despues_del_cierre_de_la_vela_abierta()
        Assert.AreEqual(CLng(11 * H + 3), BackgroundStreamingSubscriber.ProximaConsulta(Velas(9 * H, 10 * H), H, 10 * H + 100))
    End Sub

    <TestMethod>
    Public Sub Sin_vela_abierta_mercado_cerrado_reintenta_en_un_minuto()
        Dim ahora As Long = 20 * H
        Assert.AreEqual(ahora + 60, BackgroundStreamingSubscriber.ProximaConsulta(Velas(8 * H, 9 * H), H, ahora))
    End Sub

    ' ---------- LiveTestMotor en modo forward ----------

    Private Shared Function Motor(modoForward As Boolean) As LiveTestMotor
        Dim m As New LiveTestMotor()
        Dim cfg As New LiveTestConfig With {.Symbol = "frxUSDJPY", .Timeframe = "1h", .DuracionVelas = 2,
                                            .VentanaIndicadores = 5, .ModoForward = modoForward}
        m.InyectarSesionExistente(1, cfg, New EstrategiaJson(New EstrategiaDinamicaDef With {.Nombre = "Prueba"}))
        Return m
    End Function

    Private Shared Function C(epoch As Long) As CandleModel
        Return New CandleModel With {.Epoch = epoch, .OpenPrice = 1D, .High = 1D, .Low = 1D, .ClosePrice = 1D}
    End Function

    <TestMethod>
    Public Sub Precarga_ignora_repetidas_y_recorta_a_la_ventana()
        Dim m = Motor(True)
        Assert.AreEqual(3, m.PrecargarBuffer({C(3 * H), C(1 * H), C(2 * H), C(2 * H)}))
        Assert.AreEqual(5, m.PrecargarBuffer({C(1 * H), C(4 * H), C(5 * H), C(6 * H), C(7 * H)}))
    End Sub

    <TestMethod>
    Public Sub Vela_recien_cerrada_se_opera_y_la_atrasada_no()
        Dim m = Motor(True)
        Dim vela = C(10 * H)                      ' cierra en 11h
        Assert.AreEqual("", m.MotivoNoOperarForward(vela, 11 * H + 10))
        Assert.AreEqual("vela atrasada", m.MotivoNoOperarForward(vela, 11 * H + LiveTestMotor.MAX_SEGUNDOS_TRAS_CIERRE + 1))
    End Sub

    <TestMethod>
    Public Sub Una_posicion_a_la_vez_salvo_si_el_cierre_se_perdio()
        Dim m = Motor(True)
        m.AgregarPendienteExterna(1, "123", "CALL", 10 * H, 1D, 1D)
        Assert.AreEqual("posicion abierta", m.MotivoNoOperarForward(C(11 * H), 12 * H + 10))
        ' Vence en 10h + 3 velas; pasados 15 min mas sin resultado ya no bloquea
        Assert.AreEqual("", m.MotivoNoOperarForward(C(13 * H), 14 * H + 10))
    End Sub

    <TestMethod>
    Public Sub Sin_modo_forward_no_cambia_nada()
        Dim m = Motor(False)
        m.AgregarPendienteExterna(1, "123", "CALL", 10 * H, 1D, 1D)
        Assert.AreEqual("", m.MotivoNoOperarForward(C(1 * H), 50 * H))
    End Sub

End Class
