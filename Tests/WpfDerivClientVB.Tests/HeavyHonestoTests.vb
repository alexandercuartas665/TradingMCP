Imports Microsoft.VisualStudio.TestTools.UnitTesting
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

''' <summary>Heavy Strategy Tester honesto: payout real, periodo ciego y conclusion de la validacion.</summary>
<TestClass>
Public Class HeavyHonestoTests

    Private Shared Function Datos() As JObject
        Return New JObject From {
            {"R_100|120m", New JObject From {{"payout_pct", 78.55}}},
            {"R_100|60m", New JObject From {{"payout_pct", 78.65}}},
            {"FRXUSDJPY|120m", New JObject From {{"payout_pct", 80.2}}}
        }
    End Function

    <TestMethod>
    Public Sub Payout_real_por_duracion_exacta()
        ' 2 velas de 1h = contrato de 120 minutos
        Assert.AreEqual(78.55D, PayoutsReales.ObtenerDe(Datos(), "R_100", "1h", 2).Value)
        Assert.AreEqual(80.2D, PayoutsReales.ObtenerDe(Datos(), "frxUSDJPY", "1h", 2).Value)
    End Sub

    <TestMethod>
    Public Sub Payout_real_sin_esa_duracion_usa_la_media_del_simbolo_y_sin_medicion_nada()
        Assert.AreEqual(78.6D, PayoutsReales.ObtenerDe(Datos(), "R_100", "4h", 2).Value)   ' 480m no medido
        Assert.IsFalse(PayoutsReales.ObtenerDe(Datos(), "frxEURUSD", "1h", 2).HasValue)
    End Sub

    <TestMethod>
    Public Sub Registrar_y_leer_payouts_reales()
        Dim ruta = IO.Path.Combine(IO.Path.GetTempPath(), "payouts_test_" & Guid.NewGuid().ToString("N") & ".json")
        PayoutsReales.RutaOverride = ruta
        Try
            PayoutsReales.Registrar(New JArray From {
                New JObject From {{"symbol", "R_100"}, {"duracion", "120m"}, {"payout_pct", 78.5}},
                New JObject From {{"symbol", "frxUSDJPY"}, {"duracion", "120m"}, {"call_error", "cerrado"}}
            }, "2026-11-14T18:00:00Z")
            Assert.AreEqual(78.5D, PayoutsReales.Obtener("R_100", "1h", 2).Value)
            Assert.IsFalse(PayoutsReales.Obtener("frxUSDJPY", "1h", 2).HasValue)   ' sin payout medido
        Finally
            PayoutsReales.RutaOverride = Nothing
            IO.File.Delete(ruta)
        End Try
    End Sub

    <TestMethod>
    Public Sub Resolver_payout_pedido_real_o_supuesto()
        Dim ruta = IO.Path.Combine(IO.Path.GetTempPath(), "payouts_res_" & Guid.NewGuid().ToString("N") & ".json")
        PayoutsReales.RutaOverride = ruta
        Try
            Assert.AreEqual(90D, PayoutsReales.Resolver(New JValue(90), "R_100", "1h", 2))      ' pedido explicito
            Assert.AreEqual(85D, PayoutsReales.Resolver(Nothing, "R_100", "1h", 2))             ' sin medicion: supuesto
            PayoutsReales.Registrar(New JArray From {New JObject From {{"symbol", "R_100"}, {"duracion", "120m"}, {"payout_pct", 78.55}}}, "x")
            Assert.AreEqual(78.55D, PayoutsReales.Resolver(Nothing, "R_100", "1h", 2))           ' medido
            Assert.AreEqual(78.55D, PayoutsReales.Resolver(New JValue("real"), "R_100", "1h", 2))
        Finally
            PayoutsReales.RutaOverride = Nothing
            IO.File.Delete(ruta)
        End Try
    End Sub

    <TestMethod>
    Public Sub Periodo_ciego_cuenta_solo_operaciones_desde_el_corte()
        Dim ops As New List(Of BacktestOperacion) From {
            New BacktestOperacion With {.EpochEntrada = 100, .Estado = "Ganado"},
            New BacktestOperacion With {.EpochEntrada = 200, .Estado = "Ganado"},
            New BacktestOperacion With {.EpochEntrada = 300, .Estado = "Perdido"},
            New BacktestOperacion With {.EpochEntrada = 400, .Estado = "Pendiente"}
        }
        Dim c = HeavyResultRow.ContarCiego(ops, 200)
        Assert.AreEqual(2, c.Item1)
        Assert.AreEqual(1, c.Item2)
        Assert.AreEqual(0, HeavyResultRow.ContarCiego(ops, 0).Item1)
    End Sub

    <TestMethod>
    Public Sub Veredicto_del_periodo_ciego_con_el_payout_de_la_fila()
        Dim r As New HeavyResultRow With {.Pay = 85D}
        r.AplicarCiego(10, 7, True)
        Assert.AreEqual("Muestra insuficiente", r.VeredictoStr)
        r.AplicarCiego(400, 260, True)            ' 65% con IC ~60-70% > 54,1%
        Assert.AreEqual("Ventaja", r.VeredictoStr)
        r.AplicarCiego(100, 48, True)
        Assert.AreEqual("Sin ventaja", r.VeredictoStr)
        r.AplicarCiego(0, 0, True)
        Assert.AreEqual("Sin datos ciegos", r.VeredictoStr)
        r.AplicarCiego(100, 48, False)
        Assert.AreEqual("", r.VeredictoStr)
    End Sub

    Private Shared Function Validar(veredictoCiego As String) As JObject
        Return New JObject From {{"punto_equilibrio", 0.5405},
                                 {"periodo_ciego", New JObject From {{"veredicto", veredictoCiego}}}}
    End Function

    Private Shared Function Robustez(ops As Integer, winRate As Double, veredicto As String) As JObject
        Return New JObject From {{"walk_forward", New JObject From {
            {"total_prueba_despues_del_corte", New JObject From {{"operaciones", ops}, {"win_rate", winRate}, {"veredicto", veredicto}}}}}}
    End Function

    <TestMethod>
    Public Sub Conclusion_solo_candidata_con_ventaja_en_el_ciego()
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("VENTAJA ESTADISTICA"), Robustez(40, 0.6, "POSITIVO, NO SIGNIFICATIVO")).StartsWith("CANDIDATA"))
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("VENTAJA ESTADISTICA"), Robustez(40, 0.45, "SIN VENTAJA")).StartsWith("SIN VENTAJA"))
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("POSITIVO, NO SIGNIFICATIVO"), Robustez(40, 0.6, "POSITIVO, NO SIGNIFICATIVO")).StartsWith("PROMETEDORA"))
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("POSITIVO, NO SIGNIFICATIVO"), Robustez(40, 0.5, "SIN VENTAJA")).StartsWith("SIN VENTAJA"))
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("MUESTRA INSUFICIENTE"), Nothing).StartsWith("MUESTRA"))
        Assert.IsTrue(ValidacionCompleta.Conclusion(Validar("SIN VENTAJA"), Nothing).StartsWith("SIN VENTAJA"))
    End Sub

End Class
