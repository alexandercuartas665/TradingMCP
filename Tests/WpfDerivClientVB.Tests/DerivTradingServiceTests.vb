Imports System.Collections.Concurrent
Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

''' <summary>
''' El servicio que mueve dinero: compra automatica (proposal -> buy), correlacion por req_id,
''' contract_id numerico, errores, gestor de riesgo y cierre de contratos. Sin Deriv: los
''' mensajes salientes se capturan y las respuestas se inyectan a mano.
''' </summary>
<TestClass>
Public Class DerivTradingServiceTests

    Private ReadOnly _enviados As New ConcurrentQueue(Of JObject)()
    Private _rutaRiesgo As String

    Private Shared ReadOnly Property Svc As DerivTradingService
        Get
            Return DerivTradingService.Instance
        End Get
    End Property

    <TestInitialize>
    Public Sub Preparar()
        _rutaRiesgo = IO.Path.Combine(IO.Path.GetTempPath(), "riesgo_svc_" & Guid.NewGuid().ToString("N") & ".json")
        RiskManager.RutaEstadoOverride = _rutaRiesgo
        RiskManager.ReiniciarParaPruebas()
        Svc.ReiniciarParaPruebas()
        Svc.EnvioPruebas = Sub(json) _enviados.Enqueue(JObject.Parse(json))
    End Sub

    <TestCleanup>
    Public Sub Limpiar()
        Svc.ReiniciarParaPruebas()
        RiskManager.ReiniciarParaPruebas()
        RiskManager.RutaEstadoOverride = Nothing
        Try
            IO.File.Delete(_rutaRiesgo)
        Catch
        End Try
    End Sub

    Private Function Enviados(clave As String) As List(Of JObject)
        Return _enviados.Where(Function(m) m(clave) IsNot Nothing).ToList()
    End Function

    Private Shared Function Compra(Optional monto As Double = 1, Optional tipo As String = "CALL") As JObject
        Return New JObject From {
            {"contract_type", tipo}, {"symbol", "R_100"}, {"amount", monto},
            {"duration", 2}, {"duration_unit", "m"}, {"_meta_estrategia", "Prueba"}
        }
    End Function

    Private Shared Sub Responder(json As JObject)
        Svc.ProcesarMensajeParaPruebas(json.ToString())
    End Sub

    Private Shared Sub ResponderPropuesta(reqId As Integer, id As String, ask As Double)
        Responder(New JObject From {
            {"msg_type", "proposal"}, {"req_id", reqId},
            {"proposal", New JObject From {{"id", id}, {"ask_price", ask}, {"payout", ask * 1.78}, {"longcode", "prueba"}}}
        })
    End Sub

    Private Shared Sub ResponderCompra(reqId As Integer, contractId As Long, tipo As String)
        Responder(New JObject From {
            {"msg_type", "buy"}, {"req_id", reqId},
            {"buy", New JObject From {{"contract_id", contractId}, {"buy_price", 1},
                                      {"shortcode", tipo & "_R_100_1.78_1794700000_2M_S0P_0"}}}
        })
    End Sub

    Private Shared Function Esperar(t As Task(Of JObject)) As JObject
        Assert.IsTrue(t.Wait(5000), "La operacion no termino")
        Return t.Result
    End Function

    <TestMethod>
    Public Sub Compra_automatica_completa_proposal_buy_y_suscripcion()
        Dim t = Svc.ExecuteTradeAsync(Compra())
        Dim prop = Enviados("proposal").Single()
        Assert.AreEqual("R_100", prop("underlying_symbol").ToString())
        Assert.AreEqual("stake", prop("basis").ToString())
        Assert.AreEqual(2, CInt(prop("duration")))
        Assert.AreEqual("m", prop("duration_unit").ToString())
        Dim reqId = CInt(prop("req_id"))

        ResponderPropuesta(reqId, "PROP-1", 1.0)
        Dim buy = Enviados("buy").Single()
        Assert.AreEqual("PROP-1", buy("buy").ToString())
        Assert.AreEqual(1.0, CDbl(buy("price")), 0.0001)
        Assert.AreEqual(reqId, CInt(buy("req_id")), "El buy debe viajar con el mismo req_id")

        ResponderCompra(reqId, 123456789L, "CALL")
        Dim r = Esperar(t)
        Assert.IsTrue(CBool(r("ok")))
        Assert.AreEqual("123456789", r("contract_id").ToString())

        ' Bug historico: contract_id como texto -> Deriv lo rechazaba y nunca llegaban los cierres
        Dim poc = Enviados("proposal_open_contract").Single()
        Assert.AreEqual(JTokenType.Integer, poc("contract_id").Type)

        Dim op = Svc.ObtenerOperaciones().Single()
        Assert.AreEqual("Prueba", op.Estrategia)
        Assert.AreEqual("Abierto", op.Estado)
        Assert.AreEqual(1, CInt(RiskManager.GetEstado()("contratos_abiertos")))
    End Sub

    <TestMethod>
    Public Sub Si_Deriv_cotiza_mas_caro_que_el_monto_no_compra()
        Dim t = Svc.ExecuteTradeAsync(Compra())
        ResponderPropuesta(CInt(Enviados("proposal").Single()("req_id")), "PROP-2", 1.25)
        Dim r = Esperar(t)
        Assert.IsFalse(CBool(r("ok")))
        StringAssert.Contains(r("error").ToString(), "mayor")
        Assert.AreEqual(0, Enviados("buy").Count)
        Assert.AreEqual(0, CInt(RiskManager.GetEstado()("compras_en_curso")), "La reserva del riesgo debe liberarse")
    End Sub

    <TestMethod>
    Public Sub Un_error_de_Deriv_resuelve_la_compra_sin_quedarse_colgada()
        Dim t = Svc.ExecuteTradeAsync(Compra())
        Dim reqId = CInt(Enviados("proposal").Single()("req_id"))
        Responder(New JObject From {
            {"msg_type", "proposal"}, {"req_id", reqId},
            {"echo_req", New JObject From {{"proposal", 1}, {"req_id", reqId}}},
            {"error", New JObject From {{"code", "ContractBuyValidationError"}, {"message", "This market is presently closed."}}}
        })
        Dim r = Esperar(t)
        Assert.IsFalse(CBool(r("ok")))
        StringAssert.Contains(r("error").ToString(), "closed")
        Assert.AreEqual(0, Enviados("buy").Count)
    End Sub

    <TestMethod>
    Public Sub Dos_compras_a_la_vez_cada_una_recibe_su_contrato()
        Dim t1 = Svc.ExecuteTradeAsync(Compra(tipo:="CALL"))
        Dim t2 = Svc.ExecuteTradeAsync(Compra(tipo:="PUT"))
        Dim props = Enviados("proposal")
        Assert.AreEqual(2, props.Count)
        Dim r1 = CInt(props.Single(Function(p) p("contract_type").ToString() = "CALL")("req_id"))
        Dim r2 = CInt(props.Single(Function(p) p("contract_type").ToString() = "PUT")("req_id"))
        Assert.AreNotEqual(r1, r2)
        ' Respuestas en orden inverso
        ResponderPropuesta(r2, "P-PUT", 1.0)
        ResponderPropuesta(r1, "P-CALL", 1.0)
        ResponderCompra(r2, 222L, "PUT")
        ResponderCompra(r1, 111L, "CALL")
        Assert.AreEqual("111", Esperar(t1)("contract_id").ToString())
        Assert.AreEqual("222", Esperar(t2)("contract_id").ToString())
    End Sub

    <TestMethod>
    Public Sub El_gestor_de_riesgo_bloquea_antes_de_enviar_nada()
        Dim r = Esperar(Svc.ExecuteTradeAsync(Compra(monto:=5)))   ' maximo por defecto: 2 USD
        Assert.IsFalse(CBool(r("ok")))
        Assert.IsTrue(CBool(r("bloqueado_por_riesgo")))
        Assert.AreEqual(0, _enviados.Count)
    End Sub

    <TestMethod>
    Public Sub Cierre_ganado_actualiza_la_operacion_y_el_riesgo()
        Dim t = Svc.ExecuteTradeAsync(Compra())
        Dim reqId = CInt(Enviados("proposal").Single()("req_id"))
        ResponderPropuesta(reqId, "PROP-3", 1.0)
        ResponderCompra(reqId, 333L, "CALL")
        Esperar(t)
        Responder(New JObject From {
            {"msg_type", "proposal_open_contract"},
            {"proposal_open_contract", New JObject From {
                {"contract_id", 333L}, {"status", "won"}, {"profit", 0.78}, {"is_sold", 1},
                {"sell_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}
        })
        Dim op = Svc.ObtenerOperaciones().Single()
        Assert.AreEqual("Ganado", op.Estado)
        Assert.AreEqual(0.78D, op.ProfitLoss)
        Dim riesgo = RiskManager.GetEstado()
        Assert.AreEqual(0, CInt(riesgo("contratos_abiertos")))
        Assert.AreEqual(0.78, CDbl(riesgo("pnl_dia")), 0.0001)
    End Sub

    <TestMethod>
    Public Sub Vender_envia_el_contract_id_como_numero()
        Dim t = Svc.ExecuteSellAsync("987654321")
        Dim sell = Enviados("sell").Single()
        Assert.AreEqual(JTokenType.Integer, sell("sell").Type)
        Assert.AreEqual(987654321L, CLng(sell("sell")))
    End Sub

    <TestMethod>
    Public Sub Las_consultas_nunca_permiten_mover_dinero_ni_suscribirse()
        For Each req In {New JObject From {{"buy", "x"}, {"price", 1}},
                         New JObject From {{"proposal", 1}, {"amount", 1}},
                         New JObject From {{"ticks_history", "R_100"}, {"sell", 1}},
                         New JObject From {{"ticks_history", "R_100"}, {"subscribe", 1}}}
            Dim r = Esperar(Svc.ConsultarAsync(req))
            Assert.IsFalse(CBool(r("ok")), req.ToString())
        Next
        Assert.AreEqual(0, _enviados.Count)
    End Sub

End Class
