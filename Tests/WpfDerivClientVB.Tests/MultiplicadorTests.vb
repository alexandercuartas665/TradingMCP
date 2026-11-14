Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

''' <summary>Backtest de multiplicadores: take profit, stop loss, stop out, comision, huecos.</summary>
<TestClass>
Public Class MultiplicadorTests

    Private Const H As Long = 3600

    ''' <summary>Vela 0 cierra en 100 (entrada); las siguientes vienen como (open, high, low, close).</summary>
    Private Shared Function Velas(ParamArray ohlc As Decimal()()) As List(Of CandleModel)
        Dim l As New List(Of CandleModel) From {New CandleModel With {.Epoch = 0, .OpenPrice = 100D, .High = 100D, .Low = 100D, .ClosePrice = 100D}}
        Dim ep As Long = H
        For Each v In ohlc
            l.Add(New CandleModel With {.Epoch = ep, .OpenPrice = v(0), .High = v(1), .Low = v(2), .ClosePrice = v(3)})
            ep += H
        Next
        Return l
    End Function

    Private Shared Function Senal(tipo As String, Optional indice As Integer = 0) As List(Of SenalMultiplicador)
        Return New List(Of SenalMultiplicador) From {New SenalMultiplicador With {.Indice = indice, .Tipo = tipo}}
    End Function

    Private Shared Function Cfg(Optional tp As Decimal = 0D, Optional sl As Decimal = 0D, Optional maxVelas As Integer = 3,
                                Optional comision As Double = 0.0) As MultiplicadorConfig
        Return New MultiplicadorConfig With {.Multiplicador = 100, .Stake = 1D, .TakeProfit = tp, .StopLoss = sl,
                                             .MaxVelas = maxVelas, .ComisionPct = comision}
    End Function

    Private Shared Function Una(velas As List(Of CandleModel), senales As List(Of SenalMultiplicador), c As MultiplicadorConfig) As OperacionMultiplicador
        Dim ops = MultiplicadorMotor.Simular(velas, senales, c, H)
        Assert.AreEqual(1, ops.Count)
        Return ops(0)
    End Function

    <TestMethod>
    Public Sub Take_profit_en_una_subida()
        ' x100: un 1% de subida vale 1 USD; el take profit de 0.5 salta antes
        Dim op = Una(Velas({100D, 101D, 99.9D, 100.8D}, {100.8D, 101D, 100.5D, 100.9D}, {100.9D, 101D, 100.8D, 101D}), Senal("CALL"), Cfg(tp:=0.5D))
        Assert.AreEqual("TP", op.Motivo)
        Assert.AreEqual(0.5D, op.Pnl)
        Assert.AreEqual(1, op.VelasEnPosicion)
    End Sub

    <TestMethod>
    Public Sub Stop_out_nunca_pierde_mas_que_el_stake()
        Dim op = Una(Velas({100D, 100.1D, 98.5D, 98.7D}, {98.7D, 99D, 98D, 98.2D}, {98.2D, 98.5D, 98D, 98.1D}), Senal("CALL"), Cfg())
        Assert.AreEqual("StopOut", op.Motivo)
        Assert.AreEqual(-1D, op.Pnl)
    End Sub

    <TestMethod>
    Public Sub Si_en_la_misma_vela_toca_tp_y_sl_cuenta_la_perdida()
        Dim op = Una(Velas({100D, 101D, 99.4D, 100D}, {100D, 100D, 100D, 100D}, {100D, 100D, 100D, 100D}), Senal("CALL"), Cfg(tp:=0.5D, sl:=0.5D))
        Assert.AreEqual("SL", op.Motivo)
        Assert.AreEqual(-0.5D, op.Pnl)
    End Sub

    <TestMethod>
    Public Sub Cierre_por_tiempo_descuenta_la_comision()
        ' x100 con comision 0.055% del nocional = 0.055 USD por dolar
        Dim op = Una(Velas({100D, 100.1D, 99.95D, 100D}, {100D, 100.2D, 99.9D, 100.2D}), Senal("CALL"), Cfg(maxVelas:=2, comision:=0.00055))
        Assert.AreEqual("Tiempo", op.Motivo)
        Assert.AreEqual(0.2D - 0.055D, op.Pnl)
    End Sub

    <TestMethod>
    Public Sub Hueco_en_contra_se_paga_al_precio_de_apertura()
        ' SL de 0.5 pero abre un 0.7% abajo: pierde 0.7
        Dim op = Una(Velas({99.3D, 99.5D, 99.2D, 99.4D}, {99.4D, 99.5D, 99.3D, 99.4D}), Senal("CALL"), Cfg(sl:=0.5D, maxVelas:=2))
        Assert.AreEqual("SL", op.Motivo)
        Assert.AreEqual(-0.7D, op.Pnl)
    End Sub

    <TestMethod>
    Public Sub Put_gana_cuando_baja()
        Dim op = Una(Velas({100D, 100.1D, 99D, 99.2D}, {99.2D, 99.3D, 99D, 99.1D}), Senal("PUT"), Cfg(tp:=0.8D, maxVelas:=2))
        Assert.AreEqual("TP", op.Motivo)
        Assert.AreEqual(0.8D, op.Pnl)
    End Sub

    <TestMethod>
    Public Sub Hueco_de_datos_descarta_la_operacion()
        Dim v = Velas({100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D})
        v(2).Epoch += H   ' falta una vela
        Dim desc As Integer = 0
        Dim ops = MultiplicadorMotor.Simular(v, Senal("CALL"), Cfg(maxVelas:=3), H, desc)
        Assert.AreEqual(0, ops.Count)
        Assert.AreEqual(1, desc)
    End Sub

    <TestMethod>
    Public Sub Una_posicion_a_la_vez()
        Dim v = Velas({100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D},
                      {100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D}, {100D, 100.1D, 99.9D, 100D})
        Dim senales As New List(Of SenalMultiplicador) From {
            New SenalMultiplicador With {.Indice = 0, .Tipo = "CALL"},
            New SenalMultiplicador With {.Indice = 1, .Tipo = "CALL"},     ' posicion abierta: se ignora
            New SenalMultiplicador With {.Indice = 3, .Tipo = "PUT"}       ' la anterior salio en la vela 3
        }
        Dim ops = MultiplicadorMotor.Simular(v, senales, Cfg(maxVelas:=3), H)
        Assert.AreEqual(2, ops.Count)
        Assert.AreEqual(3, ops(1).IndiceEntrada)
    End Sub

    <TestMethod>
    Public Sub Resumen_mide_la_ventaja_por_el_resultado_medio()
        Dim ganadoras = Enumerable.Range(0, 20).Select(Function(i) New OperacionMultiplicador With {.Pnl = 1.5D, .Motivo = "TP"})
        Dim perdedoras = Enumerable.Range(0, 20).Select(Function(i) New OperacionMultiplicador With {.Pnl = -0.5D, .Motivo = "SL"})
        Dim r = MultiplicadorMotor.Resumen(ganadoras.Concat(perdedoras).ToList())
        Assert.AreEqual(0.5, CDbl(r("win_rate")), 0.0001)          ' acierta la mitad...
        Assert.AreEqual(0.5, CDbl(r("pnl_medio")), 0.0001)         ' ...y aun asi gana 0.5 por operacion
        Assert.AreEqual("VENTAJA ESTADISTICA", r("veredicto").ToString())
        Assert.AreEqual(3.0, CDbl(r("profit_factor")), 0.0001)
    End Sub

End Class
