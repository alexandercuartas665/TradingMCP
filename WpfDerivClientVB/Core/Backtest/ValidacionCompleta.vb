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

        ''' <summary>Velas de una fuente (Deriv por defecto; Dukascopy = historico largo externo).</summary>
        Public Function CargarVelasAsync(symbol As String, timeframe As String, Optional fuente As String = FuentesVelas.DERIV) As Task(Of List(Of CandleModel))
            Dim repo = New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Return repo.GetCandlesRangoAsync(FuentesVelas.Normalizar(fuente), symbol, timeframe, 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
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
                                            Optional corridasRuido As Integer = 30, Optional nivelRuido As Double = 0.1,
                                            Optional fuente As String = FuentesVelas.DERIV) As Task(Of JObject)
            Dim cfg As New BacktestConfig With {
                .Symbol = symbol, .Timeframe = timeframe, .DuracionVelas = duracion,
                .MinConfianza = confianza, .PayoutPct = payout, .VentanaIndicadores = 100, .UnaPosicionALaVez = True
            }
            cfg.Cuentas.Add(New BacktestCuenta With {.NombreCuenta = "robustez", .BalanceInicial = 1000D, .MontoPorOperacion = 1D})
            Dim g = If(grilla, New List(Of Integer) From {50, 55, 60, 65, 70})
            Dim velas = Await CargarVelasAsync(symbol, timeframe, fuente)
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
                    {"fuente", FuentesVelas.Normalizar(fuente)}, {"duracion_velas", duracion}, {"min_confianza", confianza}, {"payout_pct", payout},
                    {"desde", Fecha(velas.First().Epoch)}, {"hasta", Fecha(velas.Last().Epoch)},
                    {"walk_forward", wf}, {"monte_carlo", mc}, {"ruido_precios", ruido}
                }
            End Function)
        End Function

        ''' <summary>La misma regla en varios mercados: totales de diseno y de periodo ciego.</summary>
        Public Async Function MultiMercadoAsync(est As IEstrategia, mercados As IEnumerable(Of Tuple(Of String, String)),
                                                duracion As Integer, confianza As Integer, payout As Decimal,
                                                corteEpoch As Long,
                                                Optional payoutPorMercado As Func(Of String, String, Decimal) = Nothing,
                                                Optional fuente As String = FuentesVelas.DERIV) As Task(Of JObject)
            Dim filas As New JArray()
            Dim totD = 0, ganD = 0, totC = 0, ganC = 0, mercadosSobreEquilibrio = 0, mercadosConDatos = 0
            Dim sumaPayoutCiego As Double = 0
            For Each m In mercados
                Dim sym = m.Item1, tf = m.Item2
                ' Con payoutPorMercado cada mercado se mide con su propio payout (p. ej. el real medido)
                Dim pay = If(payoutPorMercado Is Nothing, payout, payoutPorMercado(sym, tf))
                Dim velas = Await CargarVelasAsync(sym, tf, fuente)
                Dim r = Await Task.Run(Function() ValidacionEstrategia.Validar(velas, est, sym, tf, duracion, confianza, pay, corteEpoch))
                filas.Add(r)
                If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then Continue For
                Dim d = r("periodo_diseno"), c = r("periodo_ciego")
                totD += CInt(d("operaciones")) : ganD += CInt(d("ganadas"))
                totC += CInt(c("operaciones")) : ganC += CInt(c("ganadas"))
                sumaPayoutCiego += CDbl(pay) * CInt(c("operaciones"))
                If CInt(c("operaciones")) > 0 Then
                    mercadosConDatos += 1
                    If CDbl(c("win_rate")) > Estadistica.PuntoEquilibrio(pay) Then mercadosSobreEquilibrio += 1
                End If
            Next
            ' El total se juzga con el payout medio ponderado por operaciones del periodo ciego
            If payoutPorMercado IsNot Nothing AndAlso totC > 0 Then payout = CDec(Math.Round(sumaPayoutCiego / totC, 2))
            Dim icD = Estadistica.IntervaloWilson(ganD, totD), icC = Estadistica.IntervaloWilson(ganC, totC)
            Return New JObject From {
                {"ok", True}, {"estrategia", est.Nombre}, {"fuente", FuentesVelas.Normalizar(fuente)},
                {"duracion_velas", duracion}, {"min_confianza", confianza}, {"payout_pct", payout},
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
                                                corteEpoch As Long,
                                                Optional fuente As String = FuentesVelas.DERIV) As Task(Of JObject)
            fuente = FuentesVelas.Normalizar(fuente)
            Dim validar = Await ValidacionEstrategia.ValidarAsync(est, symbol, timeframe, duracion, confianza, payout, corteEpoch, fuente:=fuente)
            Dim robustez = Await RobustezAsync(est, symbol, timeframe, duracion, confianza, payout, corteEpoch, fuente:=fuente)
            Dim series = Await New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString()).ListarSeriesAsync(fuente)
            Dim mercados = series.Where(Function(s) s.Item2 = timeframe).Select(Function(s) Tuple.Create(s.Item1, s.Item2)).ToList()
            Dim multi = Await MultiMercadoAsync(est, mercados, duracion, confianza, payout, corteEpoch, fuente:=fuente)
            Dim informe = InformeTexto(validar, robustez, multi)
            If fuente <> FuentesVelas.DERIV Then
                informe = String.Format("FUENTE DE VELAS: {0} (historico externo; precios parecidos pero no identicos a los de Deriv: confirmar con forward test en Deriv).", fuente) &
                          Environment.NewLine & informe
            End If
            Return New JObject From {
                {"ok", True}, {"fuente", fuente}, {"conclusion", Conclusion(validar, robustez)},
                {"informe", informe},
                {"validar", validar}, {"robustez", robustez}, {"multimercado", multi}
            }
        End Function

        ''' <summary>
        ''' Embudo para ideas nuevas: registra la hipotesis (con su motivo) ANTES de probarla, corre la
        ''' validacion completa y corrige el veredicto del periodo ciego por el numero de hipotesis
        ''' probadas hasta hoy (con muchas pruebas, alguna sale bien por azar).
        ''' </summary>
        Public Async Function ProbarHipotesisAsync(est As IEstrategia, symbol As String, timeframe As String,
                                                   duracion As Integer, confianza As Integer, payout As Decimal,
                                                   corteEpoch As Long, motivo As String,
                                                   Optional fuente As String = FuentesVelas.DERIV) As Task(Of JObject)
            If String.IsNullOrWhiteSpace(motivo) OrElse motivo.Trim().Length < 15 Then
                Return New JObject From {{"ok", False}, {"error", "Escribe el motivo (por que deberia funcionar) antes de probar: minimo 15 caracteres."}}
            End If
            Dim repo As New HipotesisRepository()
            Dim reg = Await repo.RegistrarAsync(est.Nombre, symbol, timeframe, duracion, confianza, payout, corteEpoch, motivo.Trim())
            Dim id = reg.Item1, repetidas = reg.Item3
            ' Pruebas que cuentan para la correccion: las registradas mas las hechas antes del registro
            Dim numero = reg.Item2 + Math.Max(0, ConfigManager.LoadDbConfig().HipotesisPrevias)
            Try
                Dim todo = Await EjecutarTodoAsync(est, symbol, timeframe, duracion, confianza, payout, corteEpoch, fuente)
                Dim ciego = todo("validar")?("periodo_ciego")
                Dim n = If(ciego IsNot Nothing, CInt(ciego("operaciones")), 0)
                Dim g = If(ciego IsNot Nothing, CInt(ciego("ganadas")), 0)
                Dim veredicto = If(ciego?("veredicto")?.ToString(), "")
                Dim corregido = Estadistica.VeredictoCorregido(g, n, payout, numero)
                Dim conclusion = ConclusionCorregida(todo("conclusion").ToString(), corregido, numero)
                Dim cab As New System.Text.StringBuilder()
                cab.AppendLine(String.Format("HIPOTESIS registrada #{0}. Motivo: {1}", reg.Item2, motivo.Trim()))
                cab.AppendLine(String.Format("Pruebas que cuentan para la correccion: {0} ({1} anteriores al registro + {2} registradas).",
                                             numero, numero - reg.Item2, reg.Item2))
                cab.AppendLine(String.Format("Veredicto del periodo ciego corregido por {0} pruebas: {1}", numero, corregido))
                If repetidas > 0 Then
                    cab.AppendLine(String.Format("AVISO: esta misma combinacion ya se habia probado {0} vez/veces. Repetir pruebas hasta que salga bien es sobreajuste.", repetidas))
                End If
                cab.AppendLine()
                Dim informe = cab.ToString() & todo("informe").ToString().Replace("CONCLUSION: " & todo("conclusion").ToString(), "CONCLUSION: " & conclusion)
                Dim res As New JObject From {
                    {"ok", True}, {"id", id}, {"numero_prueba", numero}, {"repetidas", repetidas},
                    {"veredicto_ciego", veredicto}, {"veredicto_corregido", corregido},
                    {"conclusion", conclusion}, {"informe", informe}
                }
                Await repo.GuardarResultadoAsync(id, "probada", veredicto, corregido, conclusion, todo)
                Return res
            Catch ex As Exception
                Dim t = repo.GuardarResultadoAsync(id, "error", "", "", ex.Message, Nothing)
                Return New JObject From {{"ok", False}, {"id", id}, {"error", ex.Message}}
            End Try
        End Function

        ''' <summary>Una "CANDIDATA" cuya ventaja no sobrevive a la correccion por pruebas multiples baja a "PROMETEDORA".</summary>
        Public Function ConclusionCorregida(conclusion As String, veredictoCorregido As String, numeroPruebas As Integer) As String
            If conclusion.StartsWith("CANDIDATA") AndAlso veredictoCorregido <> "VENTAJA ESTADISTICA" Then
                Return String.Format("PROMETEDORA, SIN DEMOSTRAR: la ventaja del periodo ciego no sobrevive a la correccion por {0} hipotesis probadas. Hace falta un forward test en demo.", numeroPruebas)
            End If
            Return conclusion
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
