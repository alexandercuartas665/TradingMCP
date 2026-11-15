' ============================================================
' ValidacionCompleta.vb
'
' Las pruebas de robustez y multimercado, reutilizables desde la
' API (/api/heavy/robustez, /api/heavy/multimercado) y desde el
' boton "Validar" del Heavy Strategy Tester:
'   - Robustez: walk-forward (separando antes/despues del corte),
'     Monte Carlo sobre las operaciones y ruido en los precios.
'   - Multimercado: la misma regla en varios mercados, diseno
'     frente a periodo ciego.
' ============================================================
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    Public Module ValidacionCompleta

        Public Function CargarVelasAsync(symbol As String, timeframe As String) As Task(Of List(Of CandleModel))
            Dim repo = New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Return repo.GetCandlesRangoAsync("Deriv", symbol, timeframe, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        End Function

        Private Function Fecha(epoch As Long) As String
            Return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-dd")
        End Function

        ''' <summary>Walk-forward + Monte Carlo + ruido en precios. corteEpoch = 0: sin separar por corte.</summary>
        Public Async Function RobustezAsync(est As IEstrategia, symbol As String, timeframe As String,
                                            duracion As Integer, confianza As Integer, payout As Decimal,
                                            corteEpoch As Long,
                                            Optional diasEntrenamiento As Integer = 365, Optional diasPrueba As Integer = 90,
                                            Optional grilla As List(Of Integer) = Nothing,
                                            Optional iteraciones As Integer = 5000,
                                            Optional corridasRuido As Integer = 30, Optional nivelRuido As Double = 0.1) As Task(Of JObject)
            Dim cfg As New BacktestConfig With {
                .Symbol = symbol, .Timeframe = timeframe, .DuracionVelas = duracion,
                .MinConfianza = confianza, .PayoutPct = payout, .VentanaIndicadores = 100, .UnaPosicionALaVez = True
            }
            cfg.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "robustez", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})
            Dim g = If(grilla, New List(Of Integer) From {50, 55, 60, 65, 70})
            Dim velas = Await CargarVelasAsync(symbol, timeframe)
            If velas.Count < 300 Then
                Return New JObject From {{"ok", False}, {"error", String.Format("Velas insuficientes ({0}) para {1} {2}.", velas.Count, symbol, timeframe)}}
            End If
            Return Await Task.Run(Function()
                ' Cache de senales: walk-forward y simulacion completa analizan las mismas velas
                Dim estCache As New EstrategiaCacheada(est)
                Dim wf = WalkForward.Ejecutar(velas, estCache, cfg, g, diasEntrenamiento, diasPrueba, 10, corteEpoch)
                Dim sim = BacktestMotor.Simular(velas, estCache, cfg)
                Dim mc = MonteCarlo.AnalizarOperaciones(sim.Operaciones, iteraciones)
                Dim ruido = MonteCarlo.RuidoEnPrecios(velas, est, cfg, corridasRuido, nivelRuido)
                Return New JObject From {
                    {"ok", True}, {"estrategia", est.Nombre}, {"symbol", symbol}, {"timeframe", timeframe},
                    {"duracion_velas", duracion}, {"min_confianza", confianza}, {"payout_pct", payout},
                    {"desde", Fecha(velas.First().Epoch)}, {"hasta", Fecha(velas.Last().Epoch)},
                    {"walk_forward", wf}, {"monte_carlo", mc}, {"ruido_precios", ruido}
                }
            End Function)
        End Function

        ''' <summary>La misma regla en varios mercados: totales de diseno y de periodo ciego.</summary>
        Public Async Function MultiMercadoAsync(est As IEstrategia, mercados As IEnumerable(Of Tuple(Of String, String)),
                                                duracion As Integer, confianza As Integer, payout As Decimal,
                                                corteEpoch As Long) As Task(Of JObject)
            Dim filas As New JArray()
            Dim totD = 0, ganD = 0, totC = 0, ganC = 0, mercadosSobreEquilibrio = 0, mercadosConDatos = 0
            For Each m In mercados
                Dim sym = m.Item1, tf = m.Item2
                Dim velas = Await CargarVelasAsync(sym, tf)
                Dim r = Await Task.Run(Function() ValidacionEstrategia.Validar(velas, est, sym, tf, duracion, confianza, payout, corteEpoch))
                filas.Add(r)
                If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then Continue For
                Dim d = r("periodo_diseno"), c = r("periodo_ciego")
                totD += CInt(d("operaciones")) : ganD += CInt(d("ganadas"))
                totC += CInt(c("operaciones")) : ganC += CInt(c("ganadas"))
                If CInt(c("operaciones")) > 0 Then
                    mercadosConDatos += 1
                    If CDbl(c("win_rate")) > Estadistica.PuntoEquilibrio(payout) Then mercadosSobreEquilibrio += 1
                End If
            Next
            Dim icD = Estadistica.IntervaloWilson(ganD, totD), icC = Estadistica.IntervaloWilson(ganC, totC)
            Return New JObject From {
                {"ok", True}, {"estrategia", est.Nombre}, {"duracion_velas", duracion}, {"min_confianza", confianza}, {"payout_pct", payout},
                {"punto_equilibrio", Math.Round(Estadistica.PuntoEquilibrio(payout), 4)},
                {"total_diseno", New JObject From {{"operaciones", totD}, {"win_rate", If(totD > 0, Math.Round(ganD / CDbl(totD), 4), 0.0)},
                                                  {"ic95_inf", Math.Round(icD.Item1, 4)}, {"ic95_sup", Math.Round(icD.Item2, 4)}}},
                {"total_ciego", New JObject From {{"operaciones", totC}, {"win_rate", If(totC > 0, Math.Round(ganC / CDbl(totC), 4), 0.0)},
                                                 {"ic95_inf", Math.Round(icC.Item1, 4)}, {"ic95_sup", Math.Round(icC.Item2, 4)},
                                                 {"veredicto", Estadistica.Veredicto(ganC, totC, payout)}}},
                {"mercados_con_datos_ciegos", mercadosConDatos},
                {"mercados_sobre_equilibrio_en_ciego", mercadosSobreEquilibrio},
                {"mercados", filas}
            }
        End Function

        ''' <summary>
        ''' Conclusion de la validacion completa (prueba ciega + walk-forward limpio). Solo
        ''' "CANDIDATA" si el periodo ciego muestra ventaja estadistica.
        ''' </summary>
        Public Function Conclusion(validar As JObject, robustez As JObject) As String
            Dim ciego = If(validar?("periodo_ciego")?("veredicto")?.ToString(), "")
            Dim eq = If(validar?("punto_equilibrio") IsNot Nothing, CDbl(validar("punto_equilibrio")), 0.5405)
            Dim wf = robustez?("walk_forward")?("total_prueba_despues_del_corte")
            Dim wfVeredicto = If(wf?("veredicto")?.ToString(), "")
            Dim wfPositivo = wf IsNot Nothing AndAlso CInt(wf("operaciones")) > 0 AndAlso CDbl(wf("win_rate")) > eq
            If ciego = "VENTAJA ESTADISTICA" AndAlso wfVeredicto <> "SIN VENTAJA" Then
                Return "CANDIDATA: ventaja en el periodo ciego. Siguiente paso: forward test en demo con las reglas fijadas de antemano."
            End If
            If ciego = "POSITIVO, NO SIGNIFICATIVO" AndAlso wfPositivo Then
                Return "PROMETEDORA, SIN DEMOSTRAR: positiva en el periodo ciego pero sin significancia. Hace falta mas muestra (forward test en demo) antes de arriesgar dinero."
            End If
            If ciego = "MUESTRA INSUFICIENTE" Then
                Return "MUESTRA INSUFICIENTE: muy pocas operaciones en el periodo ciego para decidir."
            End If
            Return "SIN VENTAJA DEMOSTRADA: no operar con dinero real."
        End Function

        ''' <summary>
        ''' Validacion completa (lo mismo que el boton Validar del Heavy Strategy Tester):
        ''' prueba ciega, robustez y la misma regla en los mercados guardados de esa temporalidad.
        ''' </summary>
        Public Async Function EjecutarTodoAsync(est As IEstrategia, symbol As String, timeframe As String,
                                                duracion As Integer, confianza As Integer, payout As Decimal,
                                                corteEpoch As Long) As Task(Of JObject)
            Dim validar = Await ValidacionEstrategia.ValidarAsync(est, symbol, timeframe, duracion, confianza, payout, corteEpoch)
            Dim robustez = Await RobustezAsync(est, symbol, timeframe, duracion, confianza, payout, corteEpoch)
            Dim series = Await New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString()).ListarSeriesAsync("Deriv")
            Dim mercados = series.Where(Function(s) s.Item2 = timeframe).Select(Function(s) Tuple.Create(s.Item1, s.Item2)).ToList()
            Dim multi = Await MultiMercadoAsync(est, mercados, duracion, confianza, payout, corteEpoch)
            Return New JObject From {
                {"ok", True}, {"conclusion", Conclusion(validar, robustez)},
                {"informe", InformeTexto(validar, robustez, multi)},
                {"validar", validar}, {"robustez", robustez}, {"multimercado", multi}
            }
        End Function

        ''' <summary>Informe en texto plano de la validacion completa.</summary>
        Public Function InformeTexto(validar As JObject, robustez As JObject, multi As JObject) As String
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Dim sb As New System.Text.StringBuilder()
            Dim pct = Function(t As JToken) If(t Is Nothing, "-", (CDbl(t) * 100).ToString("F1", inv) & "%")
            Dim linea = Function(t As JToken) If(t Is Nothing, "sin datos",
                String.Format(inv, "{0} ops  {1}  IC95 {2}-{3}  -> {4}", t("operaciones"), pct(t("win_rate")),
                              pct(t("ic95_inf")), pct(t("ic95_sup")), t("veredicto")))
            If validar Is Nothing OrElse validar("ok") Is Nothing OrElse Not CBool(validar("ok")) Then
                Return "No se pudo validar: " & If(validar?("error")?.ToString(), "sin respuesta")
            End If
            sb.AppendLine(String.Format(inv, "VALIDACION COMPLETA - {0}  {1} {2}  confianza {3}  duracion {4} velas",
                                        validar("estrategia"), validar("symbol"), validar("timeframe"), validar("min_confianza"), validar("duracion_velas")))
            sb.AppendLine(String.Format(inv, "Payout {0}%  ->  minimo para no perder {1}.  Periodo ciego desde {2}.",
                                        validar("payout_pct"), pct(validar("punto_equilibrio")), validar("corte_utc")))
            sb.AppendLine()
            sb.AppendLine("1) Prueba ciega (las reglas no vieron estos datos)")
            sb.AppendLine("   Diseno: " & linea(validar("periodo_diseno")))
            sb.AppendLine("   Ciego:  " & linea(validar("periodo_ciego")))
            sb.AppendLine()
            sb.AppendLine("2) Walk-forward (365 dias de entrenamiento / 90 de prueba)")
            If robustez Is Nothing OrElse robustez("ok") Is Nothing OrElse Not CBool(robustez("ok")) Then
                sb.AppendLine("   No disponible: " & If(robustez?("error")?.ToString(), "sin respuesta"))
            Else
                Dim wf = robustez("walk_forward")
                sb.AppendLine("   Total:                    " & linea(wf("total_prueba")))
                If wf("total_prueba_despues_del_corte") IsNot Nothing Then
                    sb.AppendLine("   Antes del corte (sesgado): " & linea(wf("total_prueba_antes_del_corte")))
                    sb.AppendLine("   Despues del corte (limpio): " & linea(wf("total_prueba_despues_del_corte")))
                End If
                Dim mc = robustez("monte_carlo"), ru = robustez("ruido_precios")
                sb.AppendLine()
                sb.AppendLine("3) Estres (mide fragilidad, no detecta sobreajuste)")
                sb.AppendLine(String.Format(inv, "   Monte Carlo: P(terminar ganando) {0}, caida maxima p95 {1} USD, peor racha p95 {2}",
                                            pct(mc?("bootstrap")?("prob_terminar_ganando")), mc?("orden")?("caida_maxima_p95"), mc?("orden")?("peor_racha_p95")))
                sb.AppendLine(String.Format(inv, "   Ruido en precios: base {0} -> media {1} (+/- {2}), {3} de {4} corridas sobre el minimo",
                                            pct(ru?("base_win_rate")), pct(ru?("ruido_win_rate_media")), pct(ru?("ruido_win_rate_desv")),
                                            ru?("corridas_sobre_equilibrio"), ru?("corridas")))
            End If
            sb.AppendLine()
            sb.AppendLine("4) Otros mercados con la misma temporalidad (periodo ciego)")
            If multi Is Nothing OrElse multi("ok") Is Nothing OrElse Not CBool(multi("ok")) Then
                sb.AppendLine("   No disponible.")
            Else
                sb.AppendLine("   Total:  " & linea(multi("total_ciego")))
                sb.AppendLine(String.Format(inv, "   Mercados sobre el minimo: {0} de {1}",
                                            multi("mercados_sobre_equilibrio_en_ciego"), multi("mercados_con_datos_ciegos")))
            End If
            sb.AppendLine()
            sb.AppendLine("CONCLUSION: " & Conclusion(validar, robustez))
            Return sb.ToString()
        End Function

    End Module

End Namespace
