' ============================================================
' MainWindow.EstrategiasDinamicas.vb
' Partial Class del MainWindow — Tab "Estrategias Dinamicas"
'
' Fuente de velas:
'   - Replay BD  : lee velas de PostgreSQL candle a candle
'                  (sin trades reales, usa el rango Desde/Hasta)
'   - Streaming  : recibe velas del WebSocket activo via
'                  el evento OnCandleCerrada (trades reales demo)
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

        ' ── Estado del tab ──────────────────────────────────────────────
        Private _dinUltimoSesionId As Integer = 0
        Private _dinLiveActivo As Boolean = False
        Private _dinRefreshTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinReplayMotor As LiveTestMotor = Nothing
        Private _dinPollTimer As System.Windows.Threading.DispatcherTimer = Nothing
        Private _dinUltimoSesionIdBD As Integer = 0   ' ultimo ID conocido en BD para detectar nuevas sesiones
        ' Guard: evita que eventos de XAML init disparen logica antes de que los controles existan
        Private _dinTabListo As Boolean = False

        ' ── Inicializacion ──────────────────────────────────────────────
        Private Sub InicializarTabDinamicas()
            dpDinDesde.SelectedDate = DateTime.Today.AddDays(-30)
            dpDinHasta.SelectedDate = DateTime.Today
            EstrategiasDinamicasRegistry.Inicializar()
            CargarListaEstrategias()
            _dinTabListo = True
            Dispatcher.BeginInvoke(New Action(Async Sub()
                                                  Await CargarActivosBDAsync()
                                              End Sub), System.Windows.Threading.DispatcherPriority.Loaded)
            ' ── Polling: detecta sesiones nuevas creadas via API/MCP cada 5s ──
            _dinPollTimer = New System.Windows.Threading.DispatcherTimer()
            _dinPollTimer.Interval = TimeSpan.FromSeconds(5)
            AddHandler _dinPollTimer.Tick, AddressOf OnPollNuevaSesionTick
            _dinPollTimer.Start()
        End Sub

        ' ── Poll: detectar nueva sesion creada externamente (MCP / API) ─────
        Private Async Sub OnPollNuevaSesionTick(sender As Object, e As EventArgs)
            ' Solo actuar cuando NO hay un backtest/live en curso para no pisar resultados
            If _dinLiveActivo OrElse Not btnDinBacktest.IsEnabled Then Return
            Try
                Dim repo As New BacktestRepository()
                Dim sesiones = Await Task.Run(Async Function()
                                                  Return Await repo.GetSesionesAsync(1)
                                              End Function)
                If sesiones.Count = 0 Then Return
                Dim ultimaId = sesiones(0).Id
                ' Primera vez: solo guardar referencia sin cargar
                If _dinUltimoSesionIdBD = 0 Then
                    _dinUltimoSesionIdBD = ultimaId
                    Return
                End If
                ' Hay sesion nueva
                If ultimaId > _dinUltimoSesionIdBD Then
                    _dinUltimoSesionIdBD = ultimaId
                    _dinUltimoSesionId = ultimaId
                    txtDinSesionId.Text = String.Format("#{0}", ultimaId)
                    Dim s = sesiones(0)
                    txtDinEstado.Text = String.Format(
                        "Nueva sesion via API — #{0} | {1} | {2}/{3} | {4} ops",
                        ultimaId, s.Estrategia, s.Symbol, s.Timeframe, s.TotalOperaciones)
                    MostrarWinRate(s.WinRate * 100D)
                    MostrarPnl(s.ProfitLossTotal)
                    txtDinOps.Text = String.Format("{0} ops", s.TotalOperaciones)
                    txtDinDrawdown.Text = String.Format("DD:{0:F2}%", s.MaxDrawdown)
                    ' Cargar version desde registry
                    Dim estReg = EstrategiasDinamicasRegistry.GetByNombre(s.Estrategia)
                    If estReg IsNot Nothing Then
                        txtDinVersion.Text = String.Format("{0} v{1}", s.Estrategia, estReg.Definicion.Version)
                    Else
                        txtDinVersion.Text = s.Estrategia
                    End If
                    txtDinBalance.Text = ""
                    ' Cargar operaciones en el grid
                    Await CargarOperacionesSesionAsync(ultimaId)
                    ActualizarLogBD(String.Format(
                        "Sesion #{0} cargada automaticamente | {1} | Win:{2:F1}% | PnL:{3:F2}",
                        ultimaId, s.Estrategia, s.WinRate * 100D, s.ProfitLossTotal),
                        If(s.WinRate >= 0.5, Colors.LightGreen, Colors.Orange))
                End If
            Catch
                ' Ignorar errores de poll silenciosamente
            End Try
        End Sub

        Private Sub CargarListaEstrategias()
            lstEstrategias.Items.Clear()
            Try
                Dim estrategias = EstrategiasDinamicasRegistry.GetAll()
                For Each est In estrategias
                    lstEstrategias.Items.Add(est.Nombre)
                Next
                If estrategias.Count > 0 Then
                    lstEstrategias.SelectedIndex = 0
                End If
            Catch ex As Exception
                ActualizarLogBD("Error cargando estrategias: " & ex.Message, Colors.Salmon)
            End Try
        End Sub

        ' ── Cambio de fuente ─────────────────────────────────────────────
        Private Async Sub cmbDinFuente_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            ' Guard: este evento puede dispararse durante InitializeComponent() cuando
            ' IsSelected="True" activa la seleccion inicial, antes de que los controles
            ' del tab esten creados. Ignoramos hasta que InicializarTabDinamicas termine.
            If Not _dinTabListo Then Return
            Dim fuente = ObtenerFuente()
            If fuente = "replay" Then
                Await CargarActivosBDAsync()
            Else
                ' Modo Streaming: el activo se escribe manualmente
                ActualizarLogBD("Streaming: el activo se obtiene del WebSocket activo. Asegurate de iniciar el streaming.", Colors.CornflowerBlue)
            End If
        End Sub

        ' ── Cargar activos disponibles en BD ─────────────────────────────
        Private Async Function CargarActivosBDAsync() As Task
            ActualizarLogBD("Consultando activos en BD...", Colors.Gray)
            Try
                Dim cs = ConfigManager.LoadDbConfig().GetConnectionString()
                Dim resumen As New List(Of String)()
                Dim simbolos As New List(Of String)()

                Using conn As New NpgsqlConnection(cs)
                    Await conn.OpenAsync()
                    Dim sql = "SELECT symbol, timeframe, COUNT(*) AS total, " &
                              "MIN(open_time) AS desde, MAX(open_time) AS hasta " &
                              "FROM public.candles WHERE LOWER(platform) = 'deriv' " &
                              "GROUP BY symbol, timeframe ORDER BY symbol, timeframe"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            While Await reader.ReadAsync()
                                Dim sym = reader.GetString(0)
                                Dim tf = reader.GetString(1)
                                Dim total = reader.GetInt64(2)
                                Dim desde = reader.GetDateTime(3)
                                Dim hasta = reader.GetDateTime(4)
                                If Not simbolos.Contains(sym) Then simbolos.Add(sym)
                                resumen.Add(String.Format("{0}/{1}: {2:N0}v ({3:dd/MM/yy}-{4:dd/MM/yy})",
                                                          sym, tf, total, desde, hasta))
                            End While
                        End Using
                    End Using
                End Using

                ' Actualizar ComboBox de activos en el UI thread
                Dim prevSym = cmbDinSymbol.Text
                cmbDinSymbol.Items.Clear()
                For Each s In simbolos
                    cmbDinSymbol.Items.Add(s)
                Next
                ' Conservar seleccion previa o seleccionar el primero
                If simbolos.Contains(prevSym) Then
                    cmbDinSymbol.Text = prevSym
                ElseIf simbolos.Count > 0 Then
                    cmbDinSymbol.SelectedIndex = 0
                End If

                If resumen.Count = 0 Then
                    ActualizarLogBD("BD OK | Sin datos en la tabla candles (platform=deriv). Descarga historico primero.", Colors.Orange)
                Else
                    ActualizarLogBD("BD OK | " & String.Join("  |  ", resumen), Colors.LightGreen)
                End If

            Catch ex As Exception
                ActualizarLogBD("Error BD: " & ex.Message, Colors.Salmon)
            End Try
        End Function

        ' ── Helpers UI ──────────────────────────────────────────────────
        Private Sub ActualizarLogBD(texto As String, color As Color)
            ' Null-check defensivo: el control puede no estar listo si se llama
            ' muy temprano (ej. desde un evento disparado durante InitializeComponent).
            If txtDinLogBD Is Nothing Then Return
            txtDinLogBD.Text = texto
            txtDinLogBD.Foreground = New SolidColorBrush(color)
        End Sub

        Private Function ObtenerFuente() As String
            Dim item = TryCast(cmbDinFuente.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "replay")
        End Function

        Private Function ObtenerSymbol() As String
            Return If(cmbDinSymbol.Text, "").Trim()
        End Function

        Private Function ObtenerTimeframe() As String
            Dim item = TryCast(cmbDinTimeframe.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item IsNot Nothing, item.Tag?.ToString(), "1h")
        End Function

        Private Shared Function FechaAEpoch(fecha As DateTime) As Long
            Return New DateTimeOffset(fecha.Year, fecha.Month, fecha.Day,
                                      0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        End Function

        ' ── Eventos de UI ───────────────────────────────────────────────
        Private Sub lstEstrategias_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
        End Sub

        Private Async Sub btnRecargarEstrategias_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            EstrategiasDinamicasRegistry.Recargar()
            CargarListaEstrategias()
            If ObtenerFuente() = "replay" Then
                Await CargarActivosBDAsync()
            End If
        End Sub

        Private Async Sub btnDinBacktest_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarBacktestAsync()
        End Sub

        Private Async Sub btnDinLiveIniciar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await IniciarLiveAsync()
        End Sub

        Private Async Sub btnDinLiveDetener_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await DetenerLiveAsync()
        End Sub

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

                ' ── Poblar tabla de operaciones ──────────────────────────────
                Dim filas = resultado.Operaciones.
                                OrderBy(Function(o) o.EpochEntrada).
                                Select(Function(o) New OperacionRow With {
                                    .Estrategia = nombre,
                                    .Version = "v" & version,
                                    .Tipo = o.Tipo,
                                    .Epoch = DateTimeOffset.FromUnixTimeSeconds(o.EpochEntrada).ToString("dd/MM HH:mm"),
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

        ' ── Cargar operaciones de sesion (ultimas 500) ───────────────────
        Private Async Function CargarOperacionesSesionAsync(sesionId As Integer) As Task
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Dim limite = 500
                Dim url = String.Format("http://localhost:{0}/api/dinamicas/operaciones?sesion_id={1}&limite={2}",
                                        _apiServer.Port, sesionId, limite)
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim resp = Await client.GetStringAsync(url)
                    Dim json = JObject.Parse(resp)
                    Dim arr = TryCast(json("operaciones"), JArray)
                    If arr IsNot Nothing Then
                        Dim totalBD  As Long = If(json("total")    IsNot Nothing, CLng(json("total")),    CLng(arr.Count))
                        Dim mostrando As Integer = If(json("mostrando") IsNot Nothing, CInt(json("mostrando")), arr.Count)
                        Dim lista As New List(Of OperacionRow)()
                        For Each item As JObject In arr
                            Dim pnlVal = CDec(item("pnl"))
                            Dim balPost = If(item("balance_post") IsNot Nothing AndAlso item("balance_post").Type <> JTokenType.Null,
                                             CDec(item("balance_post")), 0D)
                            lista.Add(New OperacionRow With {
                                .Estrategia   = item("estrategia")?.ToString(),
                                .Version      = item("version")?.ToString(),
                                .Tipo         = item("tipo")?.ToString(),
                                .Epoch        = DateTimeOffset.FromUnixTimeSeconds(CLng(item("epoch"))).ToString("dd/MM HH:mm"),
                                .Entrada      = String.Format("{0:F5}", CDec(item("entrada"))),
                                .Salida       = String.Format("{0:F5}", CDec(item("salida"))),
                                .Estado       = item("estado")?.ToString(),
                                .Pnl          = String.Format("{0:+0.00;-0.00;0.00}", pnlVal),
                                .Confianza    = String.Format("{0}%", CInt(item("confianza"))),
                                .BalanceInicio = "",
                                .Balance      = If(balPost > 0, String.Format("${0:N2}", balPost), ""),
                                .Cuenta       = item("cuenta")?.ToString()
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

        ' ── Live test — despacho segun fuente ───────────────────────────
        Private Async Function IniciarLiveAsync() As Task
            If lstEstrategias.SelectedItem Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona una estrategia de la lista.", "Live Test")
                Return
            End If
            If ObtenerFuente() = "replay" Then
                Await IniciarReplayBDAsync()
            Else
                Await IniciarLiveStreamingAsync()
            End If
        End Function

        ' ── Live test — Replay BD ────────────────────────────────────────
        Private Async Function IniciarReplayBDAsync() As Task
            Dim nombre = lstEstrategias.SelectedItem.ToString()
            Dim estrategia = EstrategiasDinamicasRegistry.GetByNombre(nombre)
            If estrategia Is Nothing Then
                txtDinEstado.Text = "Estrategia no encontrada: " & nombre
                Return
            End If

            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()

            If String.IsNullOrWhiteSpace(symbol) Then
                System.Windows.MessageBox.Show("Selecciona un activo de la lista.", "Replay BD")
                Return
            End If

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

            Dim config As New LiveTestConfig() With {
                .Symbol = symbol,
                .Timeframe = timeframe,
                .DuracionVelas = duracion,
                .PayoutPct = payout,
                .MinConfianza = confianza,
                .MontoOperacion = monto
            }

            _dinReplayMotor = New LiveTestMotor()
            ' Sin ExecuteTrade → simulacion pura, sin trades reales en Deriv
            btnDinLiveIniciar.IsEnabled = False
            btnDinLiveDetener.IsEnabled = True
            txtDinEstado.Text = "Iniciando Replay BD..."
            txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
            txtDinWinRate.Text = "" : txtDinPnl.Text = "" : txtDinOps.Text = "" : txtDinDrawdown.Text = ""
            txtDinVersion.Text = String.Format("{0} v{1}", nombre, estrategia.Definicion.Version)
            txtDinBalance.Text = ""
            dgDinOperaciones.ItemsSource = Nothing

            Try
                Dim sesionId = Await _dinReplayMotor.IniciarAsync(config, estrategia)
                _dinUltimoSesionId = sesionId
                _dinLiveActivo = True
                txtDinSesionId.Text = "ID:" & sesionId

                ' Leer velas de BD
                ActualizarLogBD(String.Format("Consultando velas {0}/{1}...", symbol, timeframe), Colors.Gray)
                Dim dbConfig = ConfigManager.LoadDbConfig()
                Dim repoCandles As New CandleRepository(dbConfig.GetConnectionString())
                Dim candles = Await repoCandles.GetCandlesRangoAsync("Deriv", symbol, timeframe, desde, hasta)

                If candles.Count = 0 Then
                    ActualizarLogBD(String.Format("Sin velas en BD para {0}/{1} en el rango seleccionado. " &
                                                  "Usa 'Descarga Historica' para importar datos.", symbol, timeframe), Colors.Orange)
                    txtDinEstado.Text = "Sin datos en BD para ese rango."
                    Await _dinReplayMotor.DetenerAsync()
                    _dinReplayMotor = Nothing
                    _dinLiveActivo = False
                    Return
                End If

                ActualizarLogBD(String.Format("BD OK | {0}/{1}: {2:N0} velas cargadas para replay.",
                                              symbol, timeframe, candles.Count), Colors.LightGreen)
                txtDinEstado.Text = String.Format("Replay BD — {0} velas | {1} {2} | Sesion #{3}",
                                                  candles.Count, nombre, symbol, sesionId)

                ' Alimentar velas una a una
                Dim procesadas As Integer = 0
                For Each candle In candles
                    If Not _dinLiveActivo Then Exit For
                    _dinReplayMotor.ProcesarCandleCerrada(candle)
                    procesadas += 1
                    If procesadas Mod 20 = 0 Then
                        Await Task.Yield()
                        ActualizarMetricasReplay(_dinReplayMotor, procesadas, candles.Count)
                    End If
                Next

                Await _dinReplayMotor.DetenerAsync()
                ActualizarMetricasReplay(_dinReplayMotor, procesadas, candles.Count)

                If _dinLiveActivo Then
                    txtDinEstado.Text = String.Format("Replay completo — {0} velas | Sesion #{1}", procesadas, sesionId)
                    txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
                Else
                    txtDinEstado.Text = String.Format("Replay detenido — {0}/{1} velas", procesadas, candles.Count)
                    txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HAD, &HC8))
                End If

                If sesionId > 0 AndAlso _apiServer IsNot Nothing AndAlso _apiServer.IsRunning Then
                    Await CargarOperacionesSesionAsync(sesionId)
                End If

            Catch ex As Exception
                txtDinEstado.Text = "Error en replay: " & ex.Message
                txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HF3, &H8B, &HA8))
                ActualizarLogBD("Error: " & ex.Message, Colors.Salmon)
            Finally
                _dinLiveActivo = False
                _dinReplayMotor = Nothing
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
            End Try
        End Function

        Private Sub ActualizarMetricasReplay(motor As LiveTestMotor, procesadas As Integer, total As Integer)
            Try
                Dim estado = motor.GetEstado()
                Dim ganadas = CInt(estado("n_ganadas"))
                Dim perdidas = CInt(estado("n_perdidas"))
                Dim totalOps = CInt(estado("total_ops"))
                Dim pnl = CDec(estado("pnl_total"))
                Dim pend = CInt(estado("pendientes"))
                txtDinOps.Text = String.Format("{0} ops ({1}G/{2}P) +{3}pend  [{4}/{5}v]",
                                               totalOps, ganadas, perdidas, pend, procesadas, total)
                MostrarPnl(pnl)
                If ganadas + perdidas > 0 Then MostrarWinRate(CDec(ganadas) / CDec(ganadas + perdidas) * 100D)
            Catch
            End Try
        End Sub

        ' ── Live test — Streaming ────────────────────────────────────────
        Private Async Function IniciarLiveStreamingAsync() As Task
            Dim nombre = lstEstrategias.SelectedItem.ToString()
            Dim symbol = ObtenerSymbol()
            Dim timeframe = ObtenerTimeframe()
            Dim payout As Decimal = 85D : Decimal.TryParse(txtDinPayout.Text, payout)
            Dim confianza As Integer = 55 : Integer.TryParse(txtDinConfianza.Text, confianza)
            Dim duracion As Integer = 5 : Integer.TryParse(txtDinDuracion.Text, duracion)
            Dim monto As Decimal = 1D : Decimal.TryParse(txtDinMonto.Text, monto)

            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then
                txtDinEstado.Text = "El servidor API no esta activo. Arrancalo en la pestana Server."
                Return
            End If

            Try
                Dim body = New JObject From {
                    {"nombre", nombre}, {"symbol", symbol}, {"timeframe", timeframe},
                    {"duracion_velas", duracion}, {"payout", payout},
                    {"min_confianza", confianza}, {"monto", monto}
                }
                Using client As New HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(30)
                    Dim content As New StringContent(body.ToString(), Encoding.UTF8, "application/json")
                    Dim httpResp = Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/iniciar", _apiServer.Port), content)
                    Dim json = JObject.Parse(Await httpResp.Content.ReadAsStringAsync())

                    If json("ok")?.ToObject(Of Boolean)() = True Then
                        _dinLiveActivo = True
                        _dinReplayMotor = Nothing
                        _dinUltimoSesionId = CInt(json("sesion_id"))
                        txtDinEstado.Text = String.Format("LIVE Streaming — Sesion #{0} | {1} {2} | Esperando velas...",
                                                          _dinUltimoSesionId, nombre, symbol)
                        txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HF5, &HC2, &HE7))
                        ActualizarLogBD(String.Format("Streaming activo — {0} {1} | Las velas llegan via WebSocket.", symbol, timeframe),
                                        Colors.CornflowerBlue)
                        btnDinLiveIniciar.IsEnabled = False
                        btnDinLiveDetener.IsEnabled = True
                        txtDinSesionId.Text = "ID:" & _dinUltimoSesionId
                        IniciarTimerRefreshLive()
                    Else
                        txtDinEstado.Text = "Error iniciando live: " & json("error")?.ToString()
                    End If
                End Using
            Catch ex As Exception
                txtDinEstado.Text = "Error: " & ex.Message
            End Try
        End Function

        ' ── Detener ──────────────────────────────────────────────────────
        Private Async Function DetenerLiveAsync() As Task
            ' Replay BD: basta bajar la bandera
            If _dinReplayMotor IsNot Nothing Then
                _dinLiveActivo = False
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
                Return
            End If
            ' Streaming via API server
            If _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Using client As New HttpClient()
                    Dim content As New StringContent("{}", Encoding.UTF8, "application/json")
                    Await client.PostAsync(
                        String.Format("http://localhost:{0}/api/dinamicas/live/detener", _apiServer.Port), content)
                End Using
                _dinLiveActivo = False
                DetenerTimerRefreshLive()
                txtDinEstado.Text = "Live streaming detenido."
                txtDinEstado.Foreground = New SolidColorBrush(Color.FromRgb(&HA6, &HAD, &HC8))
                btnDinLiveIniciar.IsEnabled = True
                btnDinLiveDetener.IsEnabled = False
                If _dinUltimoSesionId > 0 Then Await CargarOperacionesSesionAsync(_dinUltimoSesionId)
            Catch ex As Exception
                txtDinEstado.Text = "Error deteniendo: " & ex.Message
            End Try
        End Function

        ' ── Timer de refresco (Streaming) ────────────────────────────────
        Private Sub IniciarTimerRefreshLive()
            If _dinRefreshTimer IsNot Nothing Then
                _dinRefreshTimer.Stop()
                RemoveHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
            End If
            _dinRefreshTimer = New System.Windows.Threading.DispatcherTimer()
            _dinRefreshTimer.Interval = TimeSpan.FromSeconds(30)
            AddHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
            _dinRefreshTimer.Start()
        End Sub

        Private Sub DetenerTimerRefreshLive()
            If _dinRefreshTimer IsNot Nothing Then
                _dinRefreshTimer.Stop()
                RemoveHandler _dinRefreshTimer.Tick, AddressOf OnRefreshLiveTick
                _dinRefreshTimer = Nothing
            End If
        End Sub

        Private Async Sub OnRefreshLiveTick(sender As Object, e As EventArgs)
            If Not _dinLiveActivo OrElse _apiServer Is Nothing OrElse Not _apiServer.IsRunning Then Return
            Try
                Dim estado = _apiServer.LiveTestMotor?.GetEstado()
                If estado IsNot Nothing Then
                    Dim ganadas = CInt(estado("n_ganadas"))
                    Dim perdidas = CInt(estado("n_perdidas"))
                    Dim totalOps = CInt(estado("total_ops"))
                    Dim pnl = CDec(estado("pnl_total"))
                    Dim pend = CInt(estado("pendientes"))
                    txtDinOps.Text = String.Format("{0} ops ({1}G/{2}P) +{3}pend", totalOps, ganadas, perdidas, pend)
                    MostrarPnl(pnl)
                    If ganadas + perdidas > 0 Then MostrarWinRate(CDec(ganadas) / CDec(ganadas + perdidas) * 100D)
                End If
                If _dinUltimoSesionId > 0 Then Await CargarOperacionesSesionAsync(_dinUltimoSesionId)
            Catch
            End Try
        End Sub

    End Class

    ' ── DTO para el DataGrid ─────────────────────────────────────────────
    Public Class OperacionRow
        Public Property Estrategia   As String
        Public Property Version      As String
        Public Property Tipo         As String
        Public Property Epoch        As String   ' formateado "dd/MM HH:mm"
        Public Property Entrada      As String   ' formateado "{0:F5}"
        Public Property Salida       As String   ' formateado "{0:F5}"
        Public Property Estado       As String
        Public Property Pnl          As String   ' formateado "{0:+0.00;-0.00}"
        Public Property Confianza    As String   ' formateado "xx%"
        Public Property BalanceInicio As String
        Public Property Balance      As String
        Public Property Cuenta       As String
    End Class

End Namespace
