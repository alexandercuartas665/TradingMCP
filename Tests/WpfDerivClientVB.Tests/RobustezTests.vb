Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

''' <summary>Walk-forward, Monte Carlo y ruido en precios.</summary>
<TestClass>
Public Class RobustezTests

    ''' <summary>CALL con confianza = minuto de la hora de la vela (0-59): permite probar la grilla.</summary>
    Private Class CallConfianzaVariable
        Implements IEstrategia
        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "CallVariable"
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

    Private Shared Function VelasDiarias(dias As Integer, Optional alza As Boolean = True) As List(Of CandleModel)
        Dim l As New List(Of CandleModel)()
        Dim epoch As Long = 1700000000
        For i = 1 To dias * 6      ' velas de 4h
            Dim p = If(alza, CDec(1000 + i), CDec(100000 - i))
            l.Add(New CandleModel With {.Epoch = epoch, .OpenPrice = p, .High = p + 0.5D, .Low = p - 0.5D, .ClosePrice = p})
            epoch += 14400
        Next
        Return l
    End Function

    Private Shared Function Cfg() As BacktestConfig
        Dim c As New BacktestConfig With {.Symbol = "R_TEST", .Timeframe = "4h", .DuracionVelas = 2, .PayoutPct = 85D, .VentanaIndicadores = 100}
        c.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "A", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})
        Return c
    End Function

    <TestMethod>
    Public Sub WalkForward_genera_ventanas_sucesivas_y_solo_suma_la_prueba()
        Dim r = WalkForward.Ejecutar(VelasDiarias(400), New CallConfianzaVariable(), Cfg(), {50, 60}, 180, 60)
        Assert.IsTrue(CBool(r("ok")))
        Dim ventanas = CType(r("ventanas"), JArray)
        Assert.IsTrue(ventanas.Count >= 3, "400 dias con 180 de entrenamiento y 60 de prueba: al menos 3 ventanas")
        Dim sumaPrueba = ventanas.Sum(Function(v) CInt(v("prueba_ops")))
        Assert.AreEqual(sumaPrueba, CInt(r("total_prueba")("operaciones")))
        ' Serie al alza: todo CALL gana en prueba
        Assert.AreEqual(1.0, CDbl(r("total_prueba")("win_rate")), 0.0001)
    End Sub

    <TestMethod>
    Public Sub WalkForward_no_opera_si_el_entrenamiento_pierde()
        ' Serie a la baja: CALL pierde en entrenamiento -> ninguna ventana opera
        Dim r = WalkForward.Ejecutar(VelasDiarias(400, alza:=False), New CallConfianzaVariable(), Cfg(), {50, 60}, 180, 60)
        Assert.AreEqual(0, CInt(r("total_prueba")("operaciones")))
        Assert.IsTrue(CType(r("ventanas"), JArray).All(Function(v) v("confianza_elegida").ToString() = "no opera"))
    End Sub

    Private Shared Function Ops(pnls As IEnumerable(Of Decimal)) As List(Of BacktestOperacion)
        Return pnls.Select(Function(p) New BacktestOperacion With {.ProfitLoss = p, .Estado = If(p > 0, "Ganado", "Perdido")}).ToList()
    End Function

    <TestMethod>
    Public Sub MonteCarlo_permutar_no_cambia_el_total_y_la_caida_real_esta_dentro_del_rango()
        Dim lista = Ops({0.85D, -1D, 0.85D, 0.85D, -1D, -1D, -1D, 0.85D, 0.85D, -1D})
        Dim r = MonteCarlo.AnalizarOperaciones(lista, 2000)
        Assert.AreEqual(-0.75, CDbl(r("resultado_total_real")), 0.001)   ' 5 x 0.85 - 5 x 1
        Dim o = r("orden")
        Assert.IsTrue(CDbl(o("caida_maxima_p95")) >= CDbl(o("caida_maxima_p50")))
        Assert.AreEqual(3.0, CDbl(o("peor_racha_real")))
    End Sub

    <TestMethod>
    Public Sub MonteCarlo_todo_ganado_da_probabilidad_1()
        Dim r = MonteCarlo.AnalizarOperaciones(Ops(Enumerable.Repeat(0.85D, 30)), 1000)
        Assert.AreEqual(1.0, CDbl(r("bootstrap")("prob_terminar_ganando")), 0.0001)
        Assert.AreEqual(0.0, CDbl(r("orden")("caida_maxima_p95")), 0.0001)
    End Sub

    <TestMethod>
    Public Sub MonteCarlo_es_reproducible_con_la_misma_semilla()
        Dim lista = Ops({0.85D, -1D, 0.85D, -1D, -1D, 0.85D, 0.85D, 0.85D, -1D, 0.85D, -1D, -1D})
        Assert.AreEqual(MonteCarlo.AnalizarOperaciones(lista, 500, 42).ToString(), MonteCarlo.AnalizarOperaciones(lista, 500, 42).ToString())
    End Sub

    <TestMethod>
    Public Sub Ruido_cero_reproduce_el_resultado_base()
        Dim r = MonteCarlo.RuidoEnPrecios(VelasDiarias(60), New CallConfianzaVariable(), Cfg(), corridas:=3, nivel:=0.0)
        Assert.AreEqual(CDbl(r("base_win_rate")), CDbl(r("ruido_win_rate_media")), 0.0001)
        Assert.AreEqual(0.0, CDbl(r("ruido_win_rate_desv")), 0.0001)
    End Sub

    ''' <summary>Cuenta cuantas veces se analiza de verdad.</summary>
    Private Class EstrategiaContadora
        Implements IEstrategia
        Public Llamadas As Integer
        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "Contadora"
            End Get
        End Property
        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return ""
            End Get
        End Property
        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Llamadas += 1
            Dim ultima = candles.Last()
            Return New EstrategiaResultado With {.Senal = If(ultima.Epoch Mod 2 = 0, "CALL", "PUT"), .Confianza = CInt(ultima.Epoch Mod 100)}
        End Function
    End Class

    <TestMethod>
    Public Sub Cache_de_senales_da_el_mismo_resultado_y_evita_recalculos()
        Dim velas = VelasDiarias(400)
        Dim sinCache = WalkForward.Ejecutar(velas, New EstrategiaContadora(), Cfg(), {50, 55, 60, 65, 70}, 180, 60)
        Dim interna As New EstrategiaContadora()
        Dim conCache = WalkForward.Ejecutar(velas, New EstrategiaCacheada(interna), Cfg(), {50, 55, 60, 65, 70}, 180, 60)
        Assert.AreEqual(sinCache("total_prueba").ToString(), conCache("total_prueba").ToString())
        Assert.IsTrue(interna.Llamadas <= velas.Count, "Cada ventana de velas se analiza una sola vez")
    End Sub

    <TestMethod>
    Public Sub WalkForward_separa_las_operaciones_por_la_fecha_de_corte()
        Dim velas = VelasDiarias(400)
        Dim corte = velas(velas.Count * 3 \ 4).Epoch
        Dim r = WalkForward.Ejecutar(velas, New CallConfianzaVariable(), Cfg(), {50}, 180, 60, 10, corte)
        Dim antes = CInt(r("total_prueba_antes_del_corte")("operaciones"))
        Dim despues = CInt(r("total_prueba_despues_del_corte")("operaciones"))
        Assert.IsTrue(antes > 0 AndAlso despues > 0)
        Assert.AreEqual(CInt(r("total_prueba")("operaciones")), antes + despues)
    End Sub

    <TestMethod>
    Public Sub Percentiles_y_caida_maxima()
        Assert.AreEqual(50.0, MonteCarlo.Percentil({0.0, 50.0, 100.0}, 50), 0.0001)
        Assert.AreEqual(3.0, MonteCarlo.CaidaMaxima({1.0, -2.0, -1.0, 4.0}), 0.0001)
        Assert.AreEqual(2, MonteCarlo.PeorRacha({1.0, -2.0, -1.0, 4.0, -1.0}))
    End Sub

End Class
