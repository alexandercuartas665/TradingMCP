Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

''' <summary>Estadistica de la validacion fuera de muestra.</summary>
<TestClass>
Public Class ValidacionTests

    <TestMethod>
    Public Sub Wilson_coincide_con_el_calculo_de_referencia()
        ' 100 de 147 (sesion de BollingerMomentum_v2): IC 95% = [0.601, 0.750]
        Dim ic = Estadistica.IntervaloWilson(100, 147)
        Assert.AreEqual(0.601, ic.Item1, 0.001)
        Assert.AreEqual(0.75, ic.Item2, 0.001)
    End Sub

    <TestMethod>
    Public Sub Wilson_extremos_y_muestra_vacia()
        Assert.AreEqual(0.0, Estadistica.IntervaloWilson(0, 0).Item1)
        Assert.AreEqual(0.0, Estadistica.IntervaloWilson(0, 50).Item1, 0.0001)
        Assert.AreEqual(1.0, Estadistica.IntervaloWilson(50, 50).Item2, 0.0001)
    End Sub

    <DataTestMethod>
    <DataRow(85.0, 0.5405)>
    <DataRow(90.0, 0.5263)>
    <DataRow(77.0, 0.565)>
    Public Sub Punto_de_equilibrio_segun_payout(payout As Double, esperado As Double)
        Assert.AreEqual(esperado, Estadistica.PuntoEquilibrio(CDec(payout)), 0.001)
    End Sub

    <TestMethod>
    Public Sub Veredictos()
        Assert.AreEqual("MUESTRA INSUFICIENTE", Estadistica.Veredicto(20, 25, 85D))
        ' 70% de 400: limite inferior ~65% > 54% => ventaja
        Assert.AreEqual("VENTAJA ESTADISTICA", Estadistica.Veredicto(280, 400, 85D))
        ' 56% de 100: por encima del equilibrio pero el IC incluye perder
        Assert.AreEqual("POSITIVO, NO SIGNIFICATIVO", Estadistica.Veredicto(56, 100, 85D))
        ' 50% de 1000: moneda al aire
        Assert.AreEqual("SIN VENTAJA", Estadistica.Veredicto(500, 1000, 85D))
    End Sub

    Private Class SiempreCall
        Implements IEstrategia
        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "SiempreCall"
            End Get
        End Property
        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return ""
            End Get
        End Property
        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Return New EstrategiaResultado With {.Senal = "CALL", .Confianza = 100}
        End Function
    End Class

    <TestMethod>
    Public Sub Validar_separa_las_operaciones_por_la_fecha_de_corte()
        ' 400 velas de 1h al alza: todo CALL gana; el corte parte la serie en dos
        Dim velas As New List(Of CandleModel)()
        Dim epoch As Long = 1700000000
        For i = 1 To 400
            velas.Add(New CandleModel With {.Epoch = epoch, .OpenPrice = i, .High = i, .Low = i, .ClosePrice = i})
            epoch += 3600
        Next
        Dim corte = velas(250).Epoch
        Dim r = ValidacionEstrategia.Validar(velas, New SiempreCall(), "R_TEST", "1h", 2, 0, 85D, corte)
        Assert.IsTrue(CBool(r("ok")))
        Dim a = r("periodo_diseno"), b = r("periodo_ciego")
        Assert.IsTrue(CInt(a("operaciones")) > 0 AndAlso CInt(b("operaciones")) > 0)
        Assert.AreEqual(1.0, CDbl(b("win_rate")), 0.0001)
        ' Una posicion a la vez con duracion 2: como mucho 1 operacion cada 2 velas
        Assert.IsTrue(CInt(a("operaciones")) + CInt(b("operaciones")) <= (400 - 100) \ 2 + 1)
        Assert.AreEqual(0.5405, CDbl(r("punto_equilibrio")), 0.001)
        Assert.IsNull(r("operaciones"), "sin pedirlo no se devuelve la lista")
    End Sub

    <TestMethod>
    Public Sub Validar_devuelve_la_lista_de_operaciones_si_se_pide()
        Dim velas As New List(Of CandleModel)()
        Dim epoch As Long = 1700000000
        For i = 1 To 400
            velas.Add(New CandleModel With {.Epoch = epoch, .OpenPrice = i, .High = i, .Low = i, .ClosePrice = i})
            epoch += 3600
        Next
        Dim r = ValidacionEstrategia.Validar(velas, New SiempreCall(), "R_TEST", "1h", 2, 0, 85D, velas(250).Epoch, True)
        Dim ops = CType(r("operaciones"), Newtonsoft.Json.Linq.JArray)
        Dim total = CInt(r("periodo_diseno")("operaciones")) + CInt(r("periodo_ciego")("operaciones"))
        Assert.AreEqual(total, ops.Count)
        Dim primera = ops(0)
        Assert.AreEqual(6, primera.Count())
        Assert.AreEqual(7200L, CLng(primera(1)) - CLng(primera(0)), "duracion 2 velas de 1h")
        Assert.AreEqual(1, CInt(primera(2)), "CALL")
        Assert.AreEqual(1, CInt(primera(3)), "al alza el CALL gana")
        Assert.IsTrue(CDec(primera(5)) > CDec(primera(4)))
    End Sub

End Class
