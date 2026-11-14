Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

''' <summary>
''' Reglas del motor de backtest, alineadas con lo que se opera en Deriv:
''' salida por tiempo, descarte por huecos, empate = perdida, una posicion a la vez.
''' </summary>
<TestClass>
Public Class BacktestMotorTests

    ''' <summary>Estrategia de prueba: siempre la misma senal con confianza fija.</summary>
    Private Class EstrategiaFija
        Implements IEstrategia
        Private ReadOnly _senal As String
        Private ReadOnly _conf As Integer
        Public Sub New(senal As String, Optional conf As Integer = 100)
            _senal = senal : _conf = conf
        End Sub
        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return "Fija_" & _senal
            End Get
        End Property
        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return ""
            End Get
        End Property
        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Return New EstrategiaResultado With {.Senal = _senal, .Confianza = _conf, .Motivo = "prueba"}
        End Function
    End Class

    Private Const Tf As Long = 3600   ' velas de 1h

    ''' <summary>Velas de 1h contiguas con los cierres dados (epoch = apertura).</summary>
    Private Shared Function Velas(cierres As IEnumerable(Of Decimal), Optional inicio As Long = 1700000000) As List(Of CandleModel)
        Dim lista As New List(Of CandleModel)()
        Dim epoch = inicio
        For Each c In cierres
            lista.Add(New CandleModel With {.Epoch = epoch, .OpenPrice = c, .High = c, .Low = c, .ClosePrice = c})
            epoch += Tf
        Next
        Return lista
    End Function

    Private Shared Function Config(Optional duracion As Integer = 2, Optional unaPosicion As Boolean = False,
                                   Optional payout As Decimal = 80D) As BacktestConfig
        Dim c As New BacktestConfig With {
            .Symbol = "R_TEST", .Timeframe = "1h", .DuracionVelas = duracion,
            .PayoutPct = payout, .MinConfianza = 0, .VentanaIndicadores = 1,
            .UnaPosicionALaVez = unaPosicion
        }
        c.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "A", .BalanceInicial = 100D, .MontoPorOperacion = 1D})
        Return c
    End Function

    <TestMethod>
    Public Sub Serie_al_alza_con_CALL_gana_todas_y_paga_el_payout()
        Dim r = BacktestMotor.Simular(Velas(Enumerable.Range(1, 20).Select(Function(i) CDec(i))), New EstrategiaFija("CALL"), Config())
        Assert.IsTrue(r.Operaciones.Count > 0)
        Assert.IsTrue(r.Operaciones.All(Function(o) o.Estado = "Ganado"))
        Assert.AreEqual(0.8D, r.Operaciones(0).ProfitLoss)
    End Sub

    <TestMethod>
    Public Sub La_salida_es_exactamente_N_velas_de_tiempo_despues()
        Dim r = BacktestMotor.Simular(Velas(Enumerable.Range(1, 10).Select(Function(i) CDec(i))), New EstrategiaFija("CALL"), Config(duracion:=3))
        For Each o In r.Operaciones
            Assert.AreEqual(3 * Tf, o.EpochSalida - o.EpochEntrada)
        Next
    End Sub

    <TestMethod>
    Public Sub Operaciones_que_cruzan_un_hueco_de_datos_se_descartan()
        ' 10 velas contiguas, hueco de 48h (fin de semana) y otras 10 contiguas
        Dim parte1 = Velas(Enumerable.Range(1, 10).Select(Function(i) CDec(i)))
        Dim parte2 = Velas(Enumerable.Range(11, 10).Select(Function(i) CDec(i)), parte1.Last().Epoch + Tf + 48 * Tf)
        Dim todas = parte1.Concat(parte2).ToList()
        Dim r = BacktestMotor.Simular(todas, New EstrategiaFija("CALL"), Config(duracion:=2))
        Assert.AreEqual(2, r.DescartadasPorHueco, "Las 2 entradas antes del hueco no tienen salida a tiempo")
        Assert.IsTrue(r.Operaciones.All(Function(o) o.EpochSalida - o.EpochEntrada = 2 * Tf))
    End Sub

    <TestMethod>
    Public Sub Empate_cuenta_como_perdida()
        Dim r = BacktestMotor.Simular(Velas(Enumerable.Repeat(5D, 10)), New EstrategiaFija("CALL"), Config())
        Assert.IsTrue(r.Operaciones.Count > 0)
        Assert.IsTrue(r.Operaciones.All(Function(o) o.Estado = "Perdido" AndAlso o.ProfitLoss = -1D))
    End Sub

    <TestMethod>
    Public Sub PUT_gana_cuando_baja()
        Dim r = BacktestMotor.Simular(Velas(Enumerable.Range(1, 10).Select(Function(i) CDec(100 - i))), New EstrategiaFija("PUT"), Config())
        Assert.IsTrue(r.Operaciones.All(Function(o) o.Estado = "Ganado"))
    End Sub

    <TestMethod>
    Public Sub Una_posicion_a_la_vez_no_solapa_operaciones()
        Dim cierres = Enumerable.Range(1, 30).Select(Function(i) CDec(i))
        Dim solapadas = BacktestMotor.Simular(Velas(cierres), New EstrategiaFija("CALL"), Config(duracion:=3, unaPosicion:=False))
        Dim unaVez = BacktestMotor.Simular(Velas(cierres), New EstrategiaFija("CALL"), Config(duracion:=3, unaPosicion:=True))
        Assert.IsTrue(unaVez.Operaciones.Count < solapadas.Operaciones.Count)
        Assert.IsTrue(unaVez.OmitidasPorPosicionAbierta > 0)
        ' Cada entrada empieza cuando termina la anterior (o despues)
        For k = 1 To unaVez.Operaciones.Count - 1
            Assert.IsTrue(unaVez.Operaciones(k).EpochEntrada >= unaVez.Operaciones(k - 1).EpochSalida)
        Next
    End Sub

    <TestMethod>
    Public Sub Filtro_de_confianza_no_opera_senales_debiles()
        Dim c = Config()
        c.MinConfianza = 60
        Dim r = BacktestMotor.Simular(Velas(Enumerable.Range(1, 10).Select(Function(i) CDec(i))), New EstrategiaFija("CALL", 50), c)
        Assert.AreEqual(0, r.Operaciones.Count)
        Assert.IsTrue(r.TotalSenales > 0)
    End Sub

    <DataTestMethod>
    <DataRow("1m", 60)>
    <DataRow("15m", 900)>
    <DataRow("1h", 3600)>
    <DataRow("4H", 14400)>
    <DataRow("1d", 86400)>
    <DataRow("xx", 0)>
    Public Sub Segundos_por_timeframe(tf As String, esperado As Integer)
        Assert.AreEqual(esperado, BacktestMotor.SegundosTimeframe(tf))
    End Sub

End Class
