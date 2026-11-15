Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

''' <summary>Campos de vela (hora, rango, cuerpo, vela anterior), "factor" en condiciones y las 3 hipotesis nuevas.</summary>
<TestClass>
Public Class HipotesisNuevasTests

    Private Shared Function Vela(epoch As Long, o As Decimal, h As Decimal, l As Decimal, c As Decimal) As CandleModel
        Return New CandleModel With {.Epoch = epoch, .OpenPrice = o, .High = h, .Low = l, .ClosePrice = c}
    End Function

    <TestMethod>
    Public Sub Campos_de_vela_hora_rango_cuerpo_y_anterior()
        ' 2026-11-16 07:00 UTC (lunes)
        Dim e7 As Long = New DateTimeOffset(2026, 11, 16, 7, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        Dim velas As New List(Of CandleModel) From {Vela(e7 - 3600, 1.1D, 1.2D, 1.0D, 1.15D), Vela(e7, 1.15D, 1.3D, 1.1D, 1.12D)}
        Dim ctx As New EvalContext()
        EstrategiaJson.AgregarCamposDeVela(ctx, velas)
        Assert.AreEqual(7D, ctx.GetValor("hora_utc"))
        Assert.AreEqual(1D, ctx.GetValor("dia_semana"))
        Assert.AreEqual(0.2D, ctx.GetValor("rango"))
        Assert.AreEqual(-0.03D, ctx.GetValor("cuerpo"))
        Assert.AreEqual(0.03D, ctx.GetValor("cuerpo_abs"))
        Assert.AreEqual(1.15D, ctx.GetValor("prev_close"))
        Assert.AreEqual(0.05D, ctx.GetValor("prev_cuerpo"))
    End Sub

    <TestMethod>
    Public Sub Factor_multiplica_el_valor_de_la_condicion()
        Dim ctx As New EvalContext()
        ctx.SetValor("rango", 3D)
        ctx.SetValor("atr", 1.4D)
        ctx.SetValor("cuerpo", -0.8D)
        Dim mayorQue2Atr = CondicionCompilador.Compilar(New Condicion With {.Campo = "rango", .Op = "mayor_que", .Valor = New JValue("atr"), .Factor = 2D})
        Assert.IsTrue(mayorQue2Atr(ctx))          ' 3 > 2,8
        ctx.SetValor("atr", 1.6D)
        Assert.IsFalse(mayorQue2Atr(ctx))         ' 3 > 3,2 no
        Dim bajista = CondicionCompilador.Compilar(New Condicion With {.Campo = "cuerpo", .Op = "menor_que", .Valor = New JValue("atr"), .Factor = -0.5D})
        Assert.IsFalse(bajista(ctx))              ' -0,8 < -0,5 x 1,6 = -0,8: no
        ctx.SetValor("cuerpo", -1D)
        Assert.IsTrue(bajista(ctx))               ' -1 < -0,8
    End Sub

    <TestMethod>
    Public Sub Sin_factor_las_condiciones_no_cambian()
        Dim ctx As New EvalContext()
        ctx.SetValor("rsi", 25D)
        Assert.IsTrue(CondicionCompilador.Compilar(New Condicion With {.Campo = "rsi", .Op = "menor_que", .Valor = New JValue(30)})(ctx))
    End Sub

    <TestMethod>
    Public Sub Las_tres_hipotesis_cargan_en_el_registro()
        EstrategiasDinamicasRegistry.Inicializar()
        For Each nombre In {"LondresMomentum_v1", "AsiaReversion_v1", "SobrerreaccionVela_v1"}
            Assert.IsNotNull(EstrategiasDinamicasRegistry.GetByNombre(nombre), nombre & " no esta en el registro")
        Next
    End Sub

    <TestMethod>
    Public Sub Sobrerreaccion_vende_tras_una_vela_alcista_enorme()
        EstrategiasDinamicasRegistry.Inicializar()
        Dim est = EstrategiasDinamicasRegistry.GetByNombre("SobrerreaccionVela_v1")
        Dim velas As New List(Of CandleModel)()
        Dim ep As Long = 1700000000
        For i = 1 To 30
            velas.Add(Vela(ep, 100D, 100.5D, 99.5D, 100D))   ' rango normal 1
            ep += 3600
        Next
        velas.Add(Vela(ep, 100D, 104D, 99.8D, 103.5D))       ' rango 4,2 > 2 x ATR, alcista
        Assert.AreEqual("PUT", est.Analizar(velas).Senal)
    End Sub

End Class
