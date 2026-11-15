' ============================================================
' MainWindow.DinBacktest.vb
' Partial Class del MainWindow — Estrategias Dinamicas: backtest directo, lista de sesiones guardadas y sus operaciones.
' (separado de MainWindow.EstrategiasDinamicas.vb sin cambios de codigo)
' ============================================================
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ── Backtest directo (no requiere API server) ────────────────────
        Private Async Function EjecutarBacktestAsync() As Task
            ' ── Validaciones ────────────────────────────────────────────────
            If lstEstrategias.SelectedItem Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona una estrategia de la lista.", "Backtest")
                Return
            End If
            Dim nombre = lstEstrategias.SelectedItem.ToString()

            Dim estrategia = EstrategiasDinamicasRegistry.GetByNombre(nombre)
            If estrategia Is Nothing Then
                System.Windows.MessageBox.Show("Estrategia no encontrada en el registry. Pulsa Recargar.", "Backtest")
                Return
            End If

            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()
            If String.IsNullOrWhiteSpace(symbol) Then
                System.Windows.MessageBox.Show("Selecciona o escribe un activo.", "Backtest")
                Return
            End If

            ' ── Leer parametros ─────────────────────────────────────────────
            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)
            Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)

            Dim desde As Long = If(dpDinDesde.SelectedDate.HasValue,
                                   FechaAEpoch(dpDinDesde.SelectedDate.Value),
                                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30L * 86400)
            Dim hasta As Long = If(dpDinHasta.SelectedDate.HasValue,
                                   FechaAEpoch(dpDinHasta.SelectedDate.Value.AddDays(1)),
                                   DateTimeOffset.UtcNow.ToUnixTimeSeconds())

            ' ── UI: inicio ──────────────────────────────────────────────────
            btnDinBacktest.IsEnabled = False
            btnDinLiveIniciar.IsEnabled = False
            txtDinWinRate.Text = "" : txtDinPnl.Text = "" : txtDinOps.Text = ""
            txtDinDrawdown.Text = "" : txtDinSesionId.Text = ""
            txtDinVersion.Text = "" : txtDinBalance.Text = ""
            dgDinOperaciones.ItemsSource = Nothing

            Dim sw = System.Diagnostics.Stopwatch.StartNew()

            ' ── Timer de progreso (actualiza cada segundo) ──────────────────
            Dim progressTimer As New System.Windows.Threading.DispatcherTimer()
            progressTimer.Interval = TimeSpan.FromSeconds(1)
            AddHandler progressTimer.Tick, Sub()
                                               txtDinEstado.Text = String.Format(
                                                   "Ejecutando backtest {0} / {1} / {2}... {3:F0}s",
                                                   nombre, symbol, timeframe, sw.Elapsed.TotalSeconds)
                                           End Sub
            progressTimer.Start()

            Try
                ' ── Configurar y ejecutar backtest en hilo de pool ───────────
                ActualizarLogBD(String.Format("Ejecutando backtest: {0} | {1}/{2} | {3}-{4}",
                                               nombre, symbol, timeframe,
                                               DateTimeOffset.FromUnixTimeSeconds(desde).ToString("dd/MM/yy"),
                                               DateTimeOffset.FromUnixTimeSeconds(hasta).ToString("dd/MM/yy")),
                                Colors.CornflowerBlue)

                Dim configBt As New BacktestConfig With {
                    .Symbol = symbol,
                    .Timeframe = timeframe,
                    .DesdeEpoch = desde,
                    .HastaEpoch = hasta,
                    .DuracionVelas = duracion,
                    .PayoutPct = payout,
                    .MinConfianza = confianza,
                    .VentanaIndicadores = 100,
                    .Cuentas = New List(Of BacktestCuenta) From {
                        New BacktestCuenta With {
                            .NombreCuenta = "Simulacion",
                            .BalanceInicial = 10000D,
                            .BalanceActual = 10000D,
                            .MontoPorOperacion = monto
                        }
                    }
                }

                ' Ejecutar en hilo de pool para no bloquear UI
                Dim resultado As BacktestResultado = Await Task.Run(
                    Async Function() As Task(Of BacktestResultado)
                        Dim motor As New BacktestMotor()
                        Return Await motor.EjecutarAsync(configBt, estrategia)
                    End Function)

                ' ── BacktestMotor ya guarda en BD y asigna Sesion.Id ────────
                _dinUltimoSesionId = resultado.Sesion.Id

                ' ── Mostrar resultados ───────────────────────────────────────
                Dim s = resultado.Sesion
                Dim wr = s.WinRate * 100D
                Dim totalOps = s.TotalOperaciones
                Dim ganadas = resultado.OperacionesGanadas.Count
                Dim perdidas = resultado.OperacionesPerdidas.Count
                Dim version = estrategia.Definicion.Version
                Dim balInicial = If(resultado.CuentasFinales.Count > 0,
                                    resultado.CuentasFinales(0).BalanceInicial, 10000D)
                Dim balFinal = If(resultado.CuentasFinales.Count > 0,
                                    resultado.CuentasFinales(0).BalanceActual, balInicial)

                txtDinEstado.Text = String.Format(
                    "Completado en {0:F1}s — Sesion #{1} — {2} senales → {3} ops",
                    sw.Elapsed.TotalSeconds, _dinUltimoSesionId, s.TotalSenales, totalOps)

                MostrarWinRate(wr)
                MostrarPnl(s.ProfitLossTotal)
                txtDinOps.Text = String.Format("{0}G / {1}P", ganadas, perdidas)
                txtDinDrawdown.Text = String.Format("DD:{0:F2}%", s.MaxDrawdown)
                txtDinSesionId.Text = String.Format("#{0}", _dinUltimoSesionId)
                txtDinVersion.Text = String.Format("{0} v{1}", nombre, version)
                txtDinBalance.Text = String.Format("${0:N0} -> ${1:N2}", balInicial, balFinal)

                ActualizarLogBD(String.Format(
                    "Backtest OK | #{0} | {1}/{2} | Win:{3:F1}% | PnL:{4:F2} | {5} ops | {6:F1}s",
                    _dinUltimoSesionId, symbol, timeframe, wr,
                    s.ProfitLossTotal, totalOps, sw.Elapsed.TotalSeconds),
                    If(wr >= 50, Colors.LightGreen, Colors.Orange))

                ' ── Refrescar lista de sesiones ─────────────────────────────
                Await CargarSesionesAsync(nombre)

                ' ── Poblar tabla de operaciones ──────────────────────────────
                Dim filas = resultado.Operaciones.
                                OrderBy(Function(o) o.EpochEntrada).
                                Select(Function(o) New OperacionRow With {
                                    .Estrategia = nombre,
                                    .Version = "v" & version,
                                    .Tipo = o.Tipo,
                                    .Epoch = DateTimeOffset.FromUnixTimeSeconds(o.EpochEntrada).ToString("dd/MM/yyyy HH:mm"),
                                    .EpochSalida = If(o.EpochSalida > 0,
                                                      DateTimeOffset.FromUnixTimeSeconds(o.EpochSalida).ToString("dd/MM/yyyy HH:mm"),
                                                      ""),
                                    .Entrada = String.Format("{0:F5}", o.PrecioEntrada),
                                    .Salida = String.Format("{0:F5}", o.PrecioSalida),
                                    .Estado = o.Estado,
                                    .Pnl = String.Format("{0:+0.00;-0.00;0.00}", o.ProfitLoss),
                                    .Confianza = String.Format("{0}%", o.Confianza),
                                    .BalanceInicio = String.Format("${0:N2}", balInicial),
                                    .Balance = String.Format("${0:N2}", o.BalancePost),
                                    .Cuenta = o.CuentaNombre
                                }).ToList()
                dgDinOperaciones.ItemsSource = filas

            Catch ex As Exception
                txtDinEstado.Text = "Error: " & ex.Message
                ActualizarLogBD("Error backtest: " & ex.Message, Colors.Salmon)
                AppLogger.Warn("[EstrategiasDin] EjecutarBacktestAsync: " & ex.Message)
            Finally
                sw.Stop()
                progressTimer.Stop()
                btnDinBacktest.IsEnabled = True
                btnDinLiveIniciar.IsEnabled = True
            End Try
        End Function

        ' ── Helpers de metricas ──────────────────────────────────────────
        Private Sub MostrarWinRate(wr As Decimal)
            txtDinWinRate.Text = String.Format("Win: {0:F1}%", wr)
            txtDinWinRate.Foreground = New SolidColorBrush(If(wr >= 50, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
        End Sub

        Private Sub MostrarPnl(pnl As Decimal)
            txtDinPnl.Text = String.Format("PnL: {0:F2}", pnl)
            txtDinPnl.Foreground = New SolidColorBrush(If(pnl >= 0, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
        End Sub

        ' ── Cargar lista de sesiones (filtrada por estrategia) ───────────
        Private Async Function CargarSesionesAsync(Optional estrategia As String = "") As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                ' Limite alto cuando se carga "todas las estrategias" para no perder
                ' sesiones de activos antiguos en el filtro en memoria.
                Dim limite As Integer = If(String.IsNullOrWhiteSpace(estrategia), 1000, 200)
                Dim url = String.Format("http://localhost:{0}/api/dinamicas/sesiones?limite={1}", _apiServer.Port, limite)
                If Not String.IsNullOrWhiteSpace(estrategia) Then
                    url &= "&estrategia=" & Uri.EscapeDataString(estrategia)
                End If
                Using client As HttpClient = NuevoClienteApiLocal()
                    client.Timeout = TimeSpan.FromSeconds(15)
                    Dim resp = Await client.GetStringAsync(url)
                    Dim json = JObject.Parse(resp)
                    Dim arr = TryCast(json("sesiones"), JArray)
                    If arr Is Nothing Then Return

                    Dim lista As New List(Of SesionRow)()
                    Dim verde = Color.FromRgb(&HA6, &HE3, &HA1)
                    Dim rojo = Color.FromRgb(&HF3, &H8B, &HA8)
                    For Each item As JObject In arr
                        Dim wr = CDec(item("win_rate"))
                        Dim pnl = CDec(item("profit_loss"))
                        Dim pf = CDec(item("profit_factor"))
                        Dim ops = CLng(item("total_ops"))
                        Dim senales = CLng(item("total_senales"))
                        Dim sid = CInt(item("id"))
                        Dim est = If(item("estrategia")?.ToString(), "")
                        Dim sym = If(item("symbol")?.ToString(), "")
                        Dim tf = If(item("timeframe")?.ToString(), "")
                        Dim dur = CInt(If(item("duracion_velas"), 0))
                        Dim pay = CDec(If(item("payout_pct"), 85))
                        Dim conf = CInt(If(item("min_confianza"), 55))
                        Dim desde = If(item("desde_str")?.ToString(), "")
                        Dim hasta = If(item("hasta_str")?.ToString(), "")
                        Dim fecha = If(item("created_at")?.ToString(), "")
                        Dim fechaCorta As String = fecha
                        Try
                            Dim dt = DateTime.ParseExact(fecha, "yyyy-MM-dd HH:mm",
                                         System.Globalization.CultureInfo.InvariantCulture)
                            fechaCorta = dt.ToString("dd/MM HH:mm")
                        Catch
                        End Try
                        Dim pnlStr = String.Format("{0:+0.00;-0.00;0.00}", pnl)
                        lista.Add(New SesionRow With {
                            .Id = sid,
                            .IdCorto = String.Format("#{0}", sid),
                            .FechaHora = fechaCorta,
                            .WinRate = String.Format("{0:F1}%", wr),
                            .WinRateNum = wr,
                            .WinBrush = New SolidColorBrush(If(wr >= 54.05, verde, rojo)),
                            .Resumen = String.Format("{0} · {1}/{2} · {3}ops · {4}",
                                                          est, sym, tf, ops, pnlStr),
                            .Estrategia = est,
                            .Symbol = sym,
                            .Timeframe = tf,
                            .DuracionV = dur,
                            .PayoutPct = pay,
                            .MinConf = conf,
                            .DesdeStr = desde,
                            .HastaStr = hasta,
                            .TotalSenales = senales,
                            .TotalOps = ops,
                            .ProfitLoss = pnl,
                            .ProfitFactor = pf
                        })
                    Next
                    _dinSesionesCompletas = lista
                    AplicarFiltroSesiones()
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error cargando sesiones: " & ex.Message, Colors.Salmon)
            End Try
        End Function

        ''' <summary>
        ''' Aplica el filtro actual de simbolo a la lista completa y refresca el ListView.
        ''' Filtro vacio ("") = mostrar todas las sesiones.
        ''' </summary>
        Private Sub AplicarFiltroSesiones()
            If lstDinSesiones Is Nothing Then Return
            Dim filtroSym = If(cmbDinFiltroSimbolo?.SelectedItem?.ToString(), "")
            Dim soloGanadoras As Boolean = (chkDinSoloGanadoras IsNot Nothing AndAlso chkDinSoloGanadoras.IsChecked = True)

            Dim resultado As IEnumerable(Of SesionRow) = _dinSesionesCompletas

            ' Filtro por symbol
            If Not String.IsNullOrEmpty(filtroSym) Then
                resultado = resultado.Where(Function(s) s.Symbol = filtroSym)
            End If

            ' Filtro "solo ganadoras": WinRate > 50% y PnL > 0
            If soloGanadoras Then
                resultado = resultado.Where(Function(s) s.WinRateNum > 50D AndAlso s.ProfitLoss > 0D)
            End If

            lstDinSesiones.ItemsSource = resultado.ToList()
        End Sub

        Private Sub cmbDinFiltroSimbolo_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            If Not _dinTabListo Then Return
            AplicarFiltroSesiones()
        End Sub

        ''' <summary>Handler unificado para checkboxes de filtro de la lista.</summary>
        Private Sub chkDinFiltroChanged(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not _dinTabListo Then Return
            AplicarFiltroSesiones()
        End Sub

        ' ── Seleccion de sesion → barra config + operaciones ─────────────
        Private Async Sub lstDinSesiones_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            Dim row = TryCast(lstDinSesiones.SelectedItem, SesionRow)
            If row Is Nothing Then
                brdDinConfig.Visibility = System.Windows.Visibility.Collapsed
                btnDinEliminar.IsEnabled = False
                Return
            End If
            _dinUltimoSesionId = row.Id
            txtDinSesionId.Text = row.IdCorto

            ' Poblar barra de configuracion
            Dim estReg = EstrategiasDinamicasRegistry.GetByNombre(row.Estrategia)
            Dim version = If(estReg IsNot Nothing, " v" & estReg.Definicion.Version, "")
            txtDinCfgId.Text = row.IdCorto
            txtDinCfgEstrategia.Text = row.Estrategia & version
            txtDinCfgSymbol.Text = String.Format("{0} / {1}", row.Symbol, row.Timeframe)
            txtDinCfgDuracion.Text = String.Format("{0} velas", row.DuracionV)
            txtDinCfgPayout.Text = String.Format("{0}%", row.PayoutPct)
            txtDinCfgConfianza.Text = String.Format("{0}%", row.MinConf)
            txtDinCfgRango.Text = If(row.DesdeStr <> "" AndAlso row.HastaStr <> "",
                                          String.Format("{0} -> {1}", row.DesdeStr, row.HastaStr), "completo")
            txtDinCfgSenales.Text = String.Format("{0:N0}", row.TotalSenales)
            txtDinCfgOps.Text = String.Format("{0:N0}", row.TotalOps)
            txtDinCfgWin.Text = String.Format("{0:F1}%", row.WinRateNum)
            txtDinCfgWin.Foreground = New SolidColorBrush(
                If(row.WinRateNum >= 54.05, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
            txtDinCfgPnl.Text = String.Format("{0:+0.00;-0.00;0.00}", row.ProfitLoss)
            txtDinCfgPnl.Foreground = New SolidColorBrush(
                If(row.ProfitLoss >= 0, Color.FromRgb(&HA6, &HE3, &HA1), Color.FromRgb(&HF3, &H8B, &HA8)))
            txtDinCfgPf.Text = String.Format("{0:F3}", row.ProfitFactor)
            brdDinConfig.Visibility = System.Windows.Visibility.Visible
            btnDinEliminar.IsEnabled = True

            ' Poblar controles de entrada para re-ejecucion
            For i As Integer = 0 To lstEstrategias.Items.Count - 1
                If lstEstrategias.Items(i)?.ToString() = row.Estrategia Then
                    lstEstrategias.SelectedIndex = i
                    Exit For
                End If
            Next
            cmbDinFuente.SelectedIndex = 0
            cmbDinSymbol.Text = row.Symbol
            For i As Integer = 0 To cmbDinTimeframe.Items.Count - 1
                Dim ci = TryCast(cmbDinTimeframe.Items(i), System.Windows.Controls.ComboBoxItem)
                If ci IsNot Nothing AndAlso ci.Tag?.ToString() = row.Timeframe Then
                    cmbDinTimeframe.SelectedIndex = i
                    Exit For
                End If
            Next
            txtDinPayout.Text = row.PayoutPct.ToString()
            txtDinConfianza.Text = row.MinConf.ToString()
            txtDinDuracion.Text = row.DuracionV.ToString()

            ' Actualizar barra de metricas superior
            MostrarWinRate(row.WinRateNum)
            MostrarPnl(row.ProfitLoss)
            txtDinOps.Text = String.Format("{0:N0} ops", row.TotalOps)
            txtDinDrawdown.Text = ""
            txtDinEstado.Text = String.Format("{0} | {1} / {2}", row.Estrategia, row.Symbol, row.Timeframe)
            txtDinVersion.Text = row.Estrategia & version

            Await CargarOperacionesSesionAsync(row.Id)
        End Sub

        ' ── Eliminar sesion seleccionada ──────────────────────────────────
        Private Async Sub btnDinEliminar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim row = TryCast(lstDinSesiones.SelectedItem, SesionRow)
            If row Is Nothing Then Return
            Dim confirm = System.Windows.MessageBox.Show(
                String.Format("Eliminar sesion {0}?{1}{2}{1}Esta accion borrara {3:N0} operaciones de la BD.",
                              row.IdCorto, Environment.NewLine, row.Resumen, row.TotalOps),
                "Confirmar eliminacion",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning)
            If confirm <> System.Windows.MessageBoxResult.Yes Then Return
            Try
                btnDinEliminar.IsEnabled = False
                Using client As HttpClient = NuevoClienteApiLocal()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim body = New JObject From {{"sesion_id", row.Id}}
                    Dim content As New StringContent(body.ToString(), Text.Encoding.UTF8, "application/json")
                    Dim resp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/sesiones/eliminar", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await resp.Content.ReadAsStringAsync())
                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        ActualizarLogBD(String.Format("Sesion {0} eliminada correctamente.", row.IdCorto), Colors.LightGreen)
                        dgDinOperaciones.ItemsSource = Nothing
                        brdDinConfig.Visibility = System.Windows.Visibility.Collapsed
                        ' Refrescar la lista
                        Dim nombreSel = If(lstEstrategias.SelectedItem?.ToString(), "")
                        Dim filtro = If(chkDinTodasEstrategias?.IsChecked = True, "", nombreSel)
                        Await CargarSesionesAsync(filtro)
                    Else
                        ActualizarLogBD("Error: " & json("error")?.ToString(), Colors.Salmon)
                        btnDinEliminar.IsEnabled = True
                    End If
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error eliminando: " & ex.Message, Colors.Salmon)
                btnDinEliminar.IsEnabled = True
            End Try
        End Sub

        ' ── Cargar operaciones de sesion (ultimas 500) ───────────────────
        Private Async Function CargarOperacionesSesionAsync(sesionId As Integer) As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Dim limite = 500
                Dim url = String.Format("http://localhost:{0}/api/dinamicas/operaciones?sesion_id={1}&limite={2}",
                                        _apiServer.Port, sesionId, limite)
                Using client As HttpClient = NuevoClienteApiLocal()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim resp = Await client.GetStringAsync(url)
                    Dim json = JObject.Parse(resp)
                    Dim arr = TryCast(json("operaciones"), JArray)
                    If arr IsNot Nothing Then
                        Dim totalBD As Long = If(json("total") IsNot Nothing, CLng(json("total")), CLng(arr.Count))
                        Dim mostrando As Integer = If(json("mostrando") IsNot Nothing, CInt(json("mostrando")), arr.Count)
                        Dim lista As New List(Of OperacionRow)()
                        For Each item As JObject In arr
                            Dim pnlVal = CDec(item("pnl"))
                            Dim balPost = If(item("balance_post") IsNot Nothing AndAlso item("balance_post").Type <> JTokenType.Null,
                                             CDec(item("balance_post")), 0D)
                            Dim epSal As Long = If(item("epoch_salida") IsNot Nothing AndAlso item("epoch_salida").Type <> JTokenType.Null,
                                                   CLng(item("epoch_salida")), 0L)
                            lista.Add(New OperacionRow With {
                                .Estrategia = item("estrategia")?.ToString(),
                                .Version = item("version")?.ToString(),
                                .Tipo = item("tipo")?.ToString(),
                                .Epoch = DateTimeOffset.FromUnixTimeSeconds(CLng(item("epoch"))).ToString("dd/MM/yyyy HH:mm"),
                                .EpochSalida = If(epSal > 0,
                                                  DateTimeOffset.FromUnixTimeSeconds(epSal).ToString("dd/MM/yyyy HH:mm"),
                                                  ""),
                                .Entrada = String.Format("{0:F5}", CDec(item("entrada"))),
                                .Salida = String.Format("{0:F5}", CDec(item("salida"))),
                                .Estado = item("estado")?.ToString(),
                                .Pnl = String.Format("{0:+0.00;-0.00;0.00}", pnlVal),
                                .Confianza = String.Format("{0}%", CInt(item("confianza"))),
                                .BalanceInicio = "",
                                .Balance = If(balPost > 0, String.Format("${0:N2}", balPost), ""),
                                .Cuenta = item("cuenta")?.ToString()
                            })
                        Next
                        dgDinOperaciones.ItemsSource = lista
                        ' Mostrar aviso si hay mas operaciones de las que se muestran
                        If totalBD > mostrando Then
                            ActualizarLogBD(String.Format(
                                "Mostrando ultimas {0:N0} de {1:N0} operaciones en sesion #{2}. " &
                                "Usa run_backtest.ps1 para ver todas via SQL.",
                                mostrando, totalBD, sesionId), Colors.CornflowerBlue)
                        Else
                            ActualizarLogBD(String.Format(
                                "Sesion #{0} — {1:N0} operaciones cargadas.", sesionId, mostrando),
                                Colors.LightGreen)
                        End If
                    End If
                End Using
            Catch ex As Exception
                ActualizarLogBD("Error cargando operaciones: " & ex.Message, Colors.Salmon)
            End Try
        End Function

    End Class

End Namespace
