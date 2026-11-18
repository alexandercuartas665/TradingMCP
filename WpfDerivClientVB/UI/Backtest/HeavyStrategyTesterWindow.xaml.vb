' ============================================================
' HeavyStrategyTesterWindow.xaml.vb
'
' Ventana principal del modulo Heavy Strategy Tester. Coordina:
'   - Carga inicial de activos (BD) y estrategias (registry).
'   - Construccion de chips multi-select.
'   - Generacion de combinaciones via HeavyTestQueue.
'   - Ejecucion paralela y refresco de resultados.
'   - Drawer detalle al click en una fila.
'   - Filtros + export CSV.
' ============================================================
Imports System.Collections.ObjectModel
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Partial Public Class HeavyStrategyTesterWindow
    Inherits Window

    ' Cola COMPARTIDA con el AgentHost: si el API headless (modo agente) esta
    ' corriendo, la ventana y el API operan sobre los mismos jobs.
    Private ReadOnly _queue As HeavyTestQueue = AgentHost.ObtenerQueue()
    Private ReadOnly _todasLasFilas As New List(Of HeavyResultRow)()
    Private ReadOnly _repo As New BacktestRepository()
    Private _filtroActual As String = "all"
    Private _clockTimer As DispatcherTimer
    Private _apiServer As HeavyApiServer = Nothing

    Private ReadOnly _capitalesDisponibles As Integer() = {100, 200, 300, 400, 500, 700, 1000}
    Private ReadOnly _timeframesDisponibles As String() = {"1m", "5m", "15m", "30m", "1h", "4h", "1d"}

    ' Selecciones (multi-select)
    Private _selAssets As New HashSet(Of String)()
    Private _selStrats As New HashSet(Of String)()
    Private _selTfs As New HashSet(Of String)()
    Private _selCaps As New HashSet(Of Integer)()

    Public Sub New()
        InitializeComponent()
        AddHandler _queue.JobFinalizado, AddressOf OnJobFinalizado
        AddHandler _queue.ColaFinalizada, AddressOf OnColaFinalizada
        AddHandler Me.Loaded, AddressOf OnWindowLoaded
        AddHandler Me.Closing, AddressOf OnWindowClosing
    End Sub

    Private Async Sub OnWindowLoaded(sender As Object, e As RoutedEventArgs)
        ' Reloj UTC
        _clockTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
        AddHandler _clockTimer.Tick, AddressOf OnTickClock
        _clockTimer.Start()
        OnTickClock(Nothing, Nothing)

        ' Defaults rangos
        dpDesde.SelectedDate = DateTime.Today.AddDays(-30)
        dpHasta.SelectedDate = DateTime.Today

        ' Chips estaticos: timeframes, capitales
        RenderChipsTimeframes()
        RenderChipsCapitales()
        CargarFuentes()

        ' Bindings cola
        lstJobs.ItemsSource = _queue.Jobs

        ' Cargar activos y estrategias en paralelo
        Try
            Await Task.WhenAll(CargarActivosAsync(), Task.Run(AddressOf CargarEstrategias))
            ActualizarEstimacion()
        Catch ex As Exception
            txtStatus.Text = "Error inicial: " & ex.Message
        End Try

        ' Cargar pruebas previas guardadas en BD (mismas tablas que MainWindow → backtest_sesiones)
        Try
            Await CargarSesionesGuardadasAsync()
        Catch ex As Exception
            txtStatus.Text = "Error cargando sesiones previas: " & ex.Message
        End Try

        ' Si el API global del modo agente ya esta corriendo, adoptarlo en la UI
        ' (mismo server, misma cola — el boton pasa a "Detener").
        Dim globalApi = AgentHost.HeavyApi
        If globalApi IsNot Nothing AndAlso globalApi.IsRunning Then
            _apiServer = globalApi
            txtApiPort.Text = globalApi.Port.ToString()
            btnApiServer.Content = "■ Detener"
            btnApiServer.Background = New SolidColorBrush(Color.FromRgb(&H3A, &H22, &H30))
            btnApiServer.Foreground = New SolidColorBrush(Color.FromRgb(&HFF, &HFF, &HFF))
            btnApiServer.BorderBrush = New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            pillApi.Text = String.Format("API: http://localhost:{0} (agente)", globalApi.Port)
        End If
    End Sub

    ''' <summary>
    ''' Carga las sesiones previamente persistidas en backtest_sesiones (mismo origen
    ''' que MainWindow.dgDinSesiones) y las muestra en el grid central. Cada fila
    ''' viene marcada como "GUARDADA" y al abrir el drawer se cargan las ops on-demand.
    ''' </summary>
    Private Async Function CargarSesionesGuardadasAsync() As Task
        Dim sesiones As List(Of BacktestSesion)
        Try
            sesiones = Await _repo.GetSesionesAsync(1000)
        Catch ex As Exception
            txtStatus.Text = "BD: " & ex.Message
            Return
        End Try
        If sesiones Is Nothing OrElse sesiones.Count = 0 Then
            txtStatus.Text = "Sin sesiones previas en BD."
            Return
        End If
        For Each s In sesiones
            Dim r = HeavyResultRow.DesdeSesion(s)
            If r IsNot Nothing Then _todasLasFilas.Add(r)
        Next
        InicializarFiltrosAvanzados()
        ReindexarYRefrescar()
        ActualizarKpis()
        txtStatus.Text = String.Format("{0} sesiones previas cargadas desde BD.", sesiones.Count)
        Await ActualizarCiegoAsync()
    End Function

    ' ── PERIODO CIEGO ───────────────────────────────────────────────

    ''' <summary>Epoch del inicio del periodo ciego (txtCorte) o 0 si esta vacio o no es una fecha.</summary>
    Private Function CorteEpoch() As Long
        Dim f As DateTime
        If DateTime.TryParseExact(txtCorte.Text.Trim(), "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture,
                                  Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, f) Then
            Return New DateTimeOffset(f).ToUnixTimeSeconds()
        End If
        Return 0
    End Function

    ''' <summary>Recalcula las columnas del periodo ciego de todas las filas (una consulta a la BD).</summary>
    Private Async Function ActualizarCiegoAsync() As Task
        Dim corte = CorteEpoch()
        Dim filas = _todasLasFilas.ToList()
        Dim conteos As New Dictionary(Of Integer, Tuple(Of Integer, Integer))()
        If corte > 0 Then
            Try
                conteos = Await _repo.ContarCiegoAsync(filas.Select(Function(r) r.SesionId), corte)
            Catch ex As Exception
                txtStatus.Text = "Periodo ciego: " & ex.Message
            End Try
        End If
        For Each r In filas
            Dim c As Tuple(Of Integer, Integer) = Nothing
            If Not conteos.TryGetValue(r.SesionId, c) Then
                ' Sin datos en BD: usar las operaciones en memoria del job (si las hay)
                c = HeavyResultRow.ContarCiego(r.JobRef?.Resultado?.Operaciones, corte)
            End If
            r.AplicarCiego(c.Item1, c.Item2, corte > 0)
        Next
    End Function

    Private Async Sub txtCorte_LostFocus(sender As Object, e As RoutedEventArgs)
        If txtCorte.Text.Trim() <> "" AndAlso CorteEpoch() = 0 Then
            txtStatus.Text = "Fecha de corte no valida: usa AAAA-MM-DD (vacio = sin periodo ciego)."
            Return
        End If
        Await ActualizarCiegoAsync()
        txtStatus.Text = If(CorteEpoch() > 0, "Periodo ciego recalculado desde " & txtCorte.Text.Trim() & ".", "Periodo ciego desactivado.")
    End Sub

    Private Sub OnWindowClosing(sender As Object, e As System.ComponentModel.CancelEventArgs)
        If _queue.EnCurso Then
            Dim r = MessageBox.Show("Hay jobs ejecutandose. ¿Cancelar y cerrar?", "Confirmar",
                                    MessageBoxButton.YesNo, MessageBoxImage.Question)
            If r <> MessageBoxResult.Yes Then
                e.Cancel = True
                Return
            End If
            _queue.Cancelar()
        End If
        If _clockTimer IsNot Nothing Then _clockTimer.Stop()
        ' Detener API si quedo prendido — EXCEPTO si es el server global del modo
        ' agente: ese vive mas alla de la ventana y lo administra AgentHost.
        Try
            If _apiServer IsNot Nothing AndAlso _apiServer.IsRunning AndAlso
               Not ReferenceEquals(_apiServer, AgentHost.HeavyApi) Then
                _apiServer.Stop()
            End If
        Catch
        End Try
    End Sub

    Private Sub OnTickClock(sender As Object, e As EventArgs)
        txtClock.Text = DateTime.UtcNow.ToString("HH:mm:ss") & " UTC"
    End Sub

    ' ── CARGA DE DATOS ──────────────────────────────────────────────

    Private Async Function CargarActivosAsync() As Task
        Dim activos As New List(Of String)()
        Try
            Dim cs = ConfigManager.LoadDbConfig().GetConnectionString()
            Using conn As New NpgsqlConnection(cs)
                Await conn.OpenAsync()
                Using cmd As New NpgsqlCommand(
                    "SELECT DISTINCT symbol FROM public.candles ORDER BY symbol;", conn)
                    Using r = Await cmd.ExecuteReaderAsync()
                        Do While Await r.ReadAsync()
                            activos.Add(r.GetString(0))
                        Loop
                    End Using
                End Using
            End Using
        Catch ex As Exception
            txtStatus.Text = "Error cargando activos: " & ex.Message
        End Try
        Dispatcher.Invoke(Sub() RenderChipsActivos(activos))
    End Function

    Private Sub CargarEstrategias()
        Try
            EstrategiasDinamicasRegistry.Inicializar()
            Dim lista = EstrategiasDinamicasRegistry.GetAll()
            Dispatcher.Invoke(Sub() RenderListaEstrategias(lista))
        Catch ex As Exception
            Dispatcher.Invoke(Sub() txtStatus.Text = "Error cargando estrategias: " & ex.Message)
        End Try
    End Sub

    ' ── RENDER CHIPS / LISTAS ───────────────────────────────────────

    Private Sub RenderChipsActivos(activos As List(Of String))
        chipsAssets.Items.Clear()
        For Each a In activos
            Dim chip As New ToggleButton With {.Content = a, .Tag = a}
            chip.Style = CType(Me.Resources("ChipStyle"), Style)
            AddHandler chip.Checked, Sub(s, e) OnChipAsset(chip, True)
            AddHandler chip.Unchecked, Sub(s, e) OnChipAsset(chip, False)
            chipsAssets.Items.Add(chip)
        Next
        ActualizarContadores()
    End Sub

    Private Sub OnChipAsset(chip As ToggleButton, marcado As Boolean)
        Dim s = chip.Tag?.ToString()
        If String.IsNullOrEmpty(s) Then Return
        If marcado Then _selAssets.Add(s) Else _selAssets.Remove(s)
        ActualizarContadores()
        ActualizarEstimacion()
    End Sub

    Private Sub RenderChipsTimeframes()
        chipsTfs.Items.Clear()
        Dim defaults As New HashSet(Of String) From {"1h"}
        For Each tf In _timeframesDisponibles
            Dim chip As New ToggleButton With {.Content = tf, .Tag = tf}
            chip.Style = CType(Me.Resources("ChipStyle"), Style)
            If defaults.Contains(tf) Then
                chip.IsChecked = True
                _selTfs.Add(tf)
            End If
            AddHandler chip.Checked, Sub(s, e) OnChipTf(chip, True)
            AddHandler chip.Unchecked, Sub(s, e) OnChipTf(chip, False)
            chipsTfs.Items.Add(chip)
        Next
    End Sub

    Private Sub OnChipTf(chip As ToggleButton, marcado As Boolean)
        Dim s = chip.Tag?.ToString()
        If String.IsNullOrEmpty(s) Then Return
        If marcado Then _selTfs.Add(s) Else _selTfs.Remove(s)
        ActualizarContadores()
        ActualizarEstimacion()
    End Sub

    Private Sub RenderChipsCapitales()
        chipsCap.Items.Clear()
        For Each c In _capitalesDisponibles
            Dim chip As New ToggleButton With {.Content = "$" & c, .Tag = c}
            chip.Style = CType(Me.Resources("ChipStyle"), Style)
            If c = 100 Then
                chip.IsChecked = True
                _selCaps.Add(c)
            End If
            AddHandler chip.Checked, Sub(s, e) OnChipCap(chip, True)
            AddHandler chip.Unchecked, Sub(s, e) OnChipCap(chip, False)
            chipsCap.Items.Add(chip)
        Next
    End Sub

    Private Sub OnChipCap(chip As ToggleButton, marcado As Boolean)
        Dim n As Integer = 0
        If Not Integer.TryParse(chip.Tag?.ToString(), n) Then Return
        If marcado Then _selCaps.Add(n) Else _selCaps.Remove(n)
        ActualizarEstimacion()
    End Sub

    Private Sub RenderListaEstrategias(lista As List(Of EstrategiaJson))
        listStrats.Children.Clear()
        ' Agrupar por una "categoria" derivada del nombre (heuristica simple)
        Dim grupos = lista.GroupBy(Function(e) CategoriaDe(e.Nombre)).
                          OrderBy(Function(g) g.Key)
        For Each g In grupos
            Dim hdr As New TextBlock With {
                .Text = g.Key,
                .Foreground = New SolidColorBrush(Color.FromRgb(&H6C, &H77, &H90)),
                .FontSize = 9,
                .FontWeight = FontWeights.Bold,
                .FontFamily = New FontFamily("Consolas"),
                .Margin = New Thickness(2, 8, 2, 4)
            }
            listStrats.Children.Add(hdr)
            For Each est In g
                Dim cb As New CheckBox With {
                    .Content = est.Nombre,
                    .Foreground = New SolidColorBrush(Color.FromRgb(&HE7, &HEC, &HF5)),
                    .FontFamily = New FontFamily("Consolas"),
                    .FontSize = 11,
                    .Tag = est.Nombre,
                    .Margin = New Thickness(4, 1, 4, 1)
                }
                AddHandler cb.Checked, Sub(s, e) OnEstrategiaCheck(cb, True)
                AddHandler cb.Unchecked, Sub(s, e) OnEstrategiaCheck(cb, False)
                listStrats.Children.Add(cb)
            Next
        Next
    End Sub

    Private Function CategoriaDe(nombre As String) As String
        Dim n = (nombre).ToLowerInvariant()
        If n.Contains("reversal") Then Return "REVERSAL"
        If n.Contains("momentum") Then Return "MOMENTUM"
        If n.Contains("cross") Or n.Contains("ema") Or n.Contains("seykota") Then Return "TREND"
        If n.Contains("diverg") Then Return "DIVERG"
        If n.Contains("pullback") Or n.Contains("stoch") Then Return "PULLBACK"
        If n.Contains("breakout") Or n.Contains("squeeze") Or n.Contains("ichimoku") Or n.Contains("donchian") Then Return "BREAKOUT"
        If n.Contains("price") Or n.Contains("engulf") Then Return "PRICE-AX"
        If n.Contains("demo") Then Return "DEMO"
        Return "OTROS"
    End Function

    Private Sub OnEstrategiaCheck(cb As CheckBox, marcado As Boolean)
        Dim s = cb.Tag?.ToString()
        If String.IsNullOrEmpty(s) Then Return
        If marcado Then _selStrats.Add(s) Else _selStrats.Remove(s)
        ActualizarContadores()
        ActualizarEstimacion()
    End Sub

    ' ── ESTIMACION ──────────────────────────────────────────────────

    Private Sub ActualizarContadores()
        lblAssetsCount.Text = _selAssets.Count & " sel"
        lblStratsCount.Text = _selStrats.Count & " sel"
    End Sub

    Private Sub ActualizarEstimacion()
        Dim sel = ConstruirSeleccion()
        Dim total = sel.TotalCombinaciones
        lblEstTotal.Text = total.ToString("N0")
        lblEstAssets.Text = sel.Activos.Count.ToString()
        lblEstStrats.Text = sel.Estrategias.Count.ToString()
        lblEstTfs.Text = sel.Timeframes.Count.ToString()
        Dim niveles = Math.Max(1, sel.Confianzas.Count) * Math.Max(1, sel.Payouts.Count) * Math.Max(1, sel.Duraciones.Count)
        lblEstRisk.Text = niveles.ToString()
        lblEstCap.Text = sel.Capitales.Count.ToString()
        ' Tiempo estimado: aprox 3s por job / concurrencia
        Dim conc As Integer = 2 : Integer.TryParse(txtConcurrencia.Text, conc)
        If conc < 1 Then conc = 1
        Dim segs = total * 3 / conc
        Dim min = segs / 60
        lblEstTime.Text = If(segs < 60, segs & " s", min & " min")
        ActualizarCoberturaFuente()
    End Sub

    ' -- FUENTE DE VELAS ----------------------------------------------

    ' Cobertura por fuente: "symbol|tf" -> (velas, primer epoch, ultimo epoch). Una consulta por fuente.
    Private ReadOnly _cobertura As New Dictionary(Of String, Dictionary(Of String, Tuple(Of Long, Long, Long)))()
    Private _versionCobertura As Integer

    Private Sub CargarFuentes()
        cmbFuente.Items.Clear()
        For Each f In FuentesVelas.Todas
            cmbFuente.Items.Add(f)
        Next
        cmbFuente.SelectedIndex = 0
    End Sub

    Private Function FuenteSeleccionada() As String
        Dim f = cmbFuente?.SelectedItem?.ToString()
        Return If(String.IsNullOrEmpty(f), FuentesVelas.DERIV, f)
    End Function

    Private Sub cmbFuente_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        ActualizarCoberturaFuente()
    End Sub

    ''' <summary>
    ''' Muestra que tiene la fuente elegida y, para cada activo y temporalidad seleccionados,
    ''' cuantas velas hay y de que fechas. Avisa si alguna combinacion no tiene velas.
    ''' </summary>
    Private Async Sub ActualizarCoberturaFuente()
        If cmbFuente Is Nothing OrElse lblCoberturaFuente Is Nothing Then Return
        Dim fuente = FuenteSeleccionada()
        _versionCobertura += 1
        Dim version = _versionCobertura
        Dim mapa As Dictionary(Of String, Tuple(Of Long, Long, Long)) = Nothing
        If Not _cobertura.TryGetValue(fuente, mapa) Then
            lblCoberturaFuente.Text = FuentesVelas.Descripcion(fuente) & " Consultando cobertura..."
            Try
                Dim repo As New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
                Dim filas = Await repo.CoberturaAsync(fuente)
                mapa = filas.ToDictionary(Function(c) c.Item1 & "|" & c.Item2, Function(c) Tuple.Create(c.Item3, c.Item4, c.Item5))
                _cobertura(fuente) = mapa
            Catch ex As Exception
                If version = _versionCobertura Then lblCoberturaFuente.Text = FuentesVelas.Descripcion(fuente) & " (no se pudo leer la cobertura: " & ex.Message & ")"
                Return
            End Try
        End If
        If version <> _versionCobertura Then Return
        lblCoberturaFuente.Text = TextoCobertura(fuente, mapa, _selAssets, _selTfs)
    End Sub

    ''' <summary>Texto de cobertura (funcion pura para poder probarla).</summary>
    Friend Shared Function TextoCobertura(fuente As String, mapa As Dictionary(Of String, Tuple(Of Long, Long, Long)),
                                          activos As IEnumerable(Of String), tfs As IEnumerable(Of String)) As String
        Dim fecha = Function(ep As Long) DateTimeOffset.FromUnixTimeSeconds(ep).UtcDateTime.ToString("yyyy-MM-dd")
        Dim lineas As New List(Of String) From {FuentesVelas.Descripcion(fuente)}
        Dim pares = (From a In activos.OrderBy(Function(x) x) From t In tfs.OrderBy(Function(x) x) Select Activo = a, Tf = t).ToList()
        Dim sinVelas = 0
        For Each p In pares
            Dim c As Tuple(Of Long, Long, Long) = Nothing
            Dim hay = mapa.TryGetValue(p.Activo & "|" & p.Tf, c)
            If Not hay Then sinVelas += 1
            If lineas.Count > 8 Then Continue For
            lineas.Add(If(hay, String.Format("{0} {1}: {2:N0} velas, {3} a {4}", p.Activo, p.Tf, c.Item1, fecha(c.Item2), fecha(c.Item3)),
                                String.Format("{0} {1}: sin velas en {2}", p.Activo, p.Tf, fuente)))
        Next
        If pares.Count > 8 Then lineas.Add(String.Format("... y {0} combinaciones mas.", pares.Count - 8))
        If sinVelas > 0 Then lineas.Add(String.Format("AVISO: {0} combinacion(es) sin velas en {1}: esas pruebas no tendran datos.", sinVelas, fuente))
        Return String.Join(Environment.NewLine, lineas)
    End Function

    Private Function ConstruirSeleccion() As HeavySelection
        Dim sel As New HeavySelection With {
            .Activos = _selAssets.ToList(),
            .Estrategias = _selStrats.ToList(),
            .Timeframes = _selTfs.ToList()
        }
        ' Capitales seleccionados
        sel.Capitales = _selCaps.Select(Function(c) CDec(c)).ToList()
        ' Rangos
        sel.Confianzas = ParseRange(txtConfMin.Text, txtConfMax.Text, txtConfStep.Text).Select(Function(d) CInt(d)).ToList()
        sel.Payouts = ParseRange(txtPayMin.Text, txtPayMax.Text, txtPayStep.Text)
        ' Payout real: cada job toma el medido; el rango solo aporta el valor de respaldo
        sel.UsarPayoutReal = chkPayoutReal.IsChecked.GetValueOrDefault()
        If sel.UsarPayoutReal AndAlso sel.Payouts.Count > 1 Then sel.Payouts = New List(Of Decimal) From {sel.Payouts(0)}
        sel.Fuente = FuenteSeleccionada()
        sel.Duraciones = ParseRange(txtDurMin.Text, txtDurMax.Text, txtDurStep.Text).Select(Function(d) CInt(d)).ToList()
        ' Fechas — DatePicker retorna DateTime con Kind=Local. Forzamos UTC con la
        ' fecha tal cual (medianoche UTC) para construir un DateTimeOffset valido.
        Dim desdeDt = If(dpDesde.SelectedDate.HasValue, dpDesde.SelectedDate.Value, DateTime.Today.AddDays(-30))
        Dim hastaDt = If(dpHasta.SelectedDate.HasValue, dpHasta.SelectedDate.Value.AddDays(1), DateTime.Today.AddDays(1))
        Dim desdeUtc = New DateTime(desdeDt.Year, desdeDt.Month, desdeDt.Day, 0, 0, 0, DateTimeKind.Utc)
        Dim hastaUtc = New DateTime(hastaDt.Year, hastaDt.Month, hastaDt.Day, 0, 0, 0, DateTimeKind.Utc)
        sel.DesdeEpoch = New DateTimeOffset(desdeUtc).ToUnixTimeSeconds()
        sel.HastaEpoch = New DateTimeOffset(hastaUtc).ToUnixTimeSeconds()
        Return sel
    End Function

    ''' <summary>
    ''' Parsea las 3 casillas con detección automática:
    '''   - Si las 3 son valores válidos y el "step" cabe dentro del rango (step ≤ max-min):
    '''       interpreta como min/max/step y genera la secuencia.
    '''   - Si el "step" es mayor que el rango (caso típico cuando el usuario escribió
    '''       3 valores discretos como 65/75/90):
    '''       interpreta los 3 campos como valores individuales [v1, v2, v3].
    '''   - Si solo hay min: devuelve [min].
    '''   - Si min y max: devuelve [min, max].
    ''' De-duplica y ordena la lista resultante.
    ''' </summary>
    Private Function ParseRange(mnTxt As String, mxTxt As String, stTxt As String) As List(Of Decimal)
        Dim r As New List(Of Decimal)()
        Dim mn, mx, st As Decimal
        Dim hasMn = Decimal.TryParse(If(mnTxt, "").Trim(), mn)
        Dim hasMx = Decimal.TryParse(If(mxTxt, "").Trim(), mx)
        Dim hasSt = Decimal.TryParse(If(stTxt, "").Trim(), st)

        If Not hasMn AndAlso Not hasMx AndAlso Not hasSt Then Return r

        ' Caso A: 3 valores y el step tiene sentido como step (cabe dentro del rango)
        If hasMn AndAlso hasMx AndAlso hasSt AndAlso st > 0D AndAlso st <= (mx - mn) Then
            Dim v = mn
            Do While v <= mx
                r.Add(v)
                v += st
                If r.Count > 100 Then Exit Do   ' guard anti-loop
            Loop
        ElseIf hasMn AndAlso hasMx AndAlso hasSt Then
            ' Caso B: 3 valores discretos (step > rango → no es un step real, son 3 cifras)
            r.Add(mn)
            r.Add(mx)
            r.Add(st)
        ElseIf hasMn AndAlso hasMx Then
            ' Caso C: 2 valores
            r.Add(mn)
            r.Add(mx)
        ElseIf hasMn Then
            ' Caso D: 1 valor
            r.Add(mn)
        End If

        ' De-duplicar y ordenar
        Return r.Distinct().OrderBy(Function(x) x).ToList()
    End Function

    ' ── BOTONES COLA ────────────────────────────────────────────────

    Private Sub btnGenerar_Click(sender As Object, e As RoutedEventArgs)
        Dim sel = ConstruirSeleccion()
        Dim n = _queue.GenerarCombinaciones(sel)
        ActualizarPillJobs()
        txtStatus.Text = String.Format("{0} combinaciones agregadas a la cola.", n)
    End Sub

    Private Sub btnLimpiar_Click(sender As Object, e As RoutedEventArgs)
        If _queue.EnCurso Then
            txtStatus.Text = "No se puede limpiar mientras hay jobs ejecutandose."
            Return
        End If
        _queue.Limpiar()
        ActualizarPillJobs()
        txtStatus.Text = "Cola limpiada."
    End Sub

    Private Async Sub btnEjecutar_Click(sender As Object, e As RoutedEventArgs)
        If _queue.EnCurso Then
            txtStatus.Text = "Ya hay una ejecucion en curso."
            Return
        End If
        Dim conc As Integer = 2
        If Not Integer.TryParse(txtConcurrencia.Text, conc) Then conc = 2
        If conc < 1 Then conc = 1
        If conc > 8 Then conc = 8

        ' Limpiar resultados previos
        _todasLasFilas.Clear()
        dgResultados.ItemsSource = Nothing

        pillEngine.Text = "Engine v0.9 · running"
        txtStatus.Text = String.Format("Ejecutando {0} jobs (concurrencia {1})...", _queue.Jobs.Count, conc)
        btnEjecutar.IsEnabled = False
        btnLimpiar.IsEnabled = False
        btnGenerar.IsEnabled = False
        btnDetener.IsEnabled = True

        Try
            Await _queue.EjecutarTodasAsync(conc)
        Finally
            btnEjecutar.IsEnabled = True
            btnLimpiar.IsEnabled = True
            btnGenerar.IsEnabled = True
            btnDetener.IsEnabled = False
            pillEngine.Text = "Engine v0.9 · idle"
        End Try
    End Sub

    Private Sub btnDetener_Click(sender As Object, e As RoutedEventArgs)
        If Not _queue.EnCurso Then
            txtStatus.Text = "No hay ejecucion en curso."
            Return
        End If
        Dim r = MessageBox.Show("¿Detener la ejecución? Los jobs pendientes serán cancelados; los que ya estén corriendo terminarán su iteración actual.",
                                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question)
        If r <> MessageBoxResult.Yes Then Return
        _queue.Cancelar()
        btnDetener.IsEnabled = False
        txtStatus.Text = "Cancelando ejecución..."
        pillEngine.Text = "Engine v0.9 · cancelando"
    End Sub

    Private Sub btnEliminarJob_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim job = TryCast(btn.DataContext, HeavyTestJob)
        If job Is Nothing Then Return
        If job.Estado = HeavyJobEstado.Ejecutando Then
            txtStatus.Text = "No se puede eliminar un job en ejecución."
            Return
        End If
        If _queue.EliminarJob(job) Then
            ' Si tiene una fila asociada en el grid, quitarla tambien
            Dim filasARemover = _todasLasFilas.Where(Function(r) r.JobRef Is job).ToList()
            For Each f In filasARemover
                _todasLasFilas.Remove(f)
            Next
            If filasARemover.Count > 0 Then
                ReindexarYRefrescar()
                ActualizarKpis()
            End If
            ActualizarPillJobs()
            txtStatus.Text = "Job " & job.Id & " eliminado."
        End If
    End Sub

    Private Sub OnJobFinalizado(job As HeavyTestJob)
        Dispatcher.BeginInvoke(Sub()
                                   If job.Estado = HeavyJobEstado.Completado Then
                                       Dim row = HeavyResultRow.DesdeJob(job)
                                       Dim corte = CorteEpoch()
                                       Dim c = HeavyResultRow.ContarCiego(job.Resultado?.Operaciones, corte)
                                       row.AplicarCiego(c.Item1, c.Item2, corte > 0)
                                       _todasLasFilas.Add(row)
                                       ReindexarYRefrescar()
                                   End If
                                   ActualizarPillJobs()
                                   ActualizarKpis()
                                   txtStatus.Text = String.Format("Job {0}: {1}", job.Id, job.EstadoStr)
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub OnColaFinalizada()
        Dispatcher.BeginInvoke(Sub()
                                   pillEngine.Text = "Engine v0.9 · idle"
                                   txtStatus.Text = "Ejecucion completa."
                                   btnEjecutar.IsEnabled = True
                                   btnLimpiar.IsEnabled = True
                                   ActualizarKpis()
                               End Sub, DispatcherPriority.Background)
    End Sub

    Private Sub ActualizarPillJobs()
        pillJobs.Text = String.Format("Jobs: {0}", _queue.Jobs.Count)
        lblQueueCount.Text = String.Format("{0} jobs", _queue.Jobs.Count)
    End Sub

    ' ── RESULTADOS ──────────────────────────────────────────────────

    Private Sub ReindexarYRefrescar()
        ' Asignar rank por PnL desc
        Dim ordenadas = _todasLasFilas.OrderByDescending(Function(r) r.Pnl).ToList()
        For i As Integer = 0 To ordenadas.Count - 1
            ordenadas(i).Rank = i + 1
        Next
        AplicarFiltro()
    End Sub

    Private Sub AplicarFiltro()
        Dim base = _todasLasFilas.OrderByDescending(Function(r) r.Pnl).AsEnumerable()

        ' 1) Filtro rapido (pills superiores)
        Select Case _filtroActual
            Case "winners" : base = base.Where(Function(r) r.EsGanadora)
            Case "losers" : base = base.Where(Function(r) Not r.EsGanadora)
            Case "top10" : base = base.Take(10)
        End Select

        ' 2) Filtros avanzados (acordeon)
        base = AplicarFiltrosAvanzados(base)

        Dim resultado = base.ToList()
        dgResultados.ItemsSource = resultado
        ActualizarResumenFiltros(resultado.Count)
    End Sub

    ' ── FILTROS AVANZADOS (acordeon) ────────────────────────────────

    Private _filtrosListos As Boolean = False

    Private Sub InicializarFiltrosAvanzados()
        ' Llamado una vez tras cargar las sesiones para poblar dropdowns
        If cmbFltActivo Is Nothing OrElse cmbFltTf Is Nothing OrElse cmbFltEstado Is Nothing Then Return

        ' Activos
        cmbFltActivo.Items.Clear()
        cmbFltActivo.Items.Add("(todos)")
        For Each a In _todasLasFilas.Select(Function(r) r.Activo).Distinct().OrderBy(Function(s) s)
            cmbFltActivo.Items.Add(a)
        Next
        cmbFltActivo.SelectedIndex = 0

        ' Timeframes
        cmbFltTf.Items.Clear()
        cmbFltTf.Items.Add("(todos)")
        For Each t In _todasLasFilas.Select(Function(r) r.Timeframe).Distinct().OrderBy(Function(s) s)
            cmbFltTf.Items.Add(t)
        Next
        cmbFltTf.SelectedIndex = 0

        ' Estados
        cmbFltEstado.Items.Clear()
        cmbFltEstado.Items.Add("(todos)")
        For Each st In _todasLasFilas.Select(Function(r) r.Estado).Distinct().OrderBy(Function(s) s)
            cmbFltEstado.Items.Add(st)
        Next
        cmbFltEstado.SelectedIndex = 0

        _filtrosListos = True
    End Sub

    Private Function AplicarFiltrosAvanzados(src As IEnumerable(Of HeavyResultRow)) As IEnumerable(Of HeavyResultRow)
        If Not _filtrosListos Then Return src

        ' Texto estrategia (contiene)
        Dim qEst = If(txtFltEstrategia.Text, "").Trim().ToLowerInvariant()
        If qEst.Length > 0 Then
            src = src.Where(Function(r) (r.Estrategia IsNot Nothing) AndAlso r.Estrategia.ToLowerInvariant().Contains(qEst))
        End If

        ' Activo
        Dim activoSel = TryCast(cmbFltActivo.SelectedItem, String)
        If activoSel IsNot Nothing AndAlso activoSel <> "(todos)" Then
            src = src.Where(Function(r) r.Activo = activoSel)
        End If
        ' TF
        Dim tfSel = TryCast(cmbFltTf.SelectedItem, String)
        If tfSel IsNot Nothing AndAlso tfSel <> "(todos)" Then
            src = src.Where(Function(r) r.Timeframe = tfSel)
        End If
        ' Estado
        Dim estSel = TryCast(cmbFltEstado.SelectedItem, String)
        If estSel IsNot Nothing AndAlso estSel <> "(todos)" Then
            src = src.Where(Function(r) r.Estado = estSel)
        End If

        ' Rangos numericos
        Dim mn As Decimal, mx As Decimal
        ' Confianza
        If TryParseDec(txtFltConfMin.Text, mn) Then src = src.Where(Function(r) CDec(r.Conf) >= mn)
        If TryParseDec(txtFltConfMax.Text, mx) Then src = src.Where(Function(r) CDec(r.Conf) <= mx)
        ' Payout
        If TryParseDec(txtFltPayMin.Text, mn) Then src = src.Where(Function(r) r.Pay >= mn)
        If TryParseDec(txtFltPayMax.Text, mx) Then src = src.Where(Function(r) r.Pay <= mx)
        ' Duracion
        If TryParseDec(txtFltDurMin.Text, mn) Then src = src.Where(Function(r) CDec(r.Dur) >= mn)
        If TryParseDec(txtFltDurMax.Text, mx) Then src = src.Where(Function(r) CDec(r.Dur) <= mx)
        ' Capital
        If TryParseDec(txtFltCapMin.Text, mn) Then src = src.Where(Function(r) r.Cap >= mn)
        If TryParseDec(txtFltCapMax.Text, mx) Then src = src.Where(Function(r) r.Cap <= mx)
        ' Win
        If TryParseDec(txtFltWinMin.Text, mn) Then src = src.Where(Function(r) r.Win >= mn)
        If TryParseDec(txtFltWinMax.Text, mx) Then src = src.Where(Function(r) r.Win <= mx)
        ' PnL
        If TryParseDec(txtFltPnlMin.Text, mn) Then src = src.Where(Function(r) r.Pnl >= mn)
        If TryParseDec(txtFltPnlMax.Text, mx) Then src = src.Where(Function(r) r.Pnl <= mx)
        ' Ops
        If TryParseDec(txtFltOpsMin.Text, mn) Then src = src.Where(Function(r) CDec(r.Ops) >= mn)
        If TryParseDec(txtFltOpsMax.Text, mx) Then src = src.Where(Function(r) CDec(r.Ops) <= mx)
        ' Sharpe min
        If TryParseDec(txtFltSharpeMin.Text, mn) Then src = src.Where(Function(r) r.Sharpe >= mn)
        ' DD max
        If TryParseDec(txtFltDDMax.Text, mx) Then src = src.Where(Function(r) r.DD <= mx)

        ' Checkboxes
        If chkFltSoloGanadoras IsNot Nothing AndAlso chkFltSoloGanadoras.IsChecked = True Then
            src = src.Where(Function(r) r.EsGanadora)
        End If
        If chkFltSoloPersistidas IsNot Nothing AndAlso chkFltSoloPersistidas.IsChecked = True Then
            src = src.Where(Function(r) r.SesionId > 0)
        End If
        If chkFltConOps IsNot Nothing AndAlso chkFltConOps.IsChecked = True Then
            src = src.Where(Function(r) r.Ops > 0)
        End If

        Return src
    End Function

    Private Function TryParseDec(s As String, ByRef v As Decimal) As Boolean
        v = 0D
        If String.IsNullOrWhiteSpace(s) Then Return False
        Return Decimal.TryParse(s.Trim().Replace(",", "."),
                                 Globalization.NumberStyles.Any,
                                 Globalization.CultureInfo.InvariantCulture, v)
    End Function

    Private Sub ActualizarResumenFiltros(visibles As Integer)
        If lblFiltroResumen Is Nothing Then Return
        Dim total = _todasLasFilas.Count
        If visibles = total Then
            lblFiltroResumen.Text = String.Format("{0:N0} filas", total)
        Else
            lblFiltroResumen.Text = String.Format("{0:N0} de {1:N0} filas (filtrado)", visibles, total)
        End If
    End Sub

    Private Sub filtroChanged(sender As Object, e As TextChangedEventArgs)
        If Not _filtrosListos Then Return
        AplicarFiltro()
    End Sub
    Private Sub filtroSelChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not _filtrosListos Then Return
        AplicarFiltro()
    End Sub
    Private Sub filtroChkChanged(sender As Object, e As RoutedEventArgs)
        If Not _filtrosListos Then Return
        AplicarFiltro()
    End Sub

    Private Sub expFiltros_Toggled(sender As Object, e As RoutedEventArgs)
        ' Repobla los dropdowns la primera vez que el usuario abre el acordeon
        If expFiltros.IsExpanded AndAlso Not _filtrosListos Then
            InicializarFiltrosAvanzados()
        End If
    End Sub

    Private Sub btnLimpiarFiltros_Click(sender As Object, e As RoutedEventArgs)
        _filtrosListos = False ' suprimir handlers
        txtFltEstrategia.Text = ""
        For Each tb In New TextBox() {txtFltConfMin, txtFltConfMax, txtFltPayMin, txtFltPayMax,
                                      txtFltDurMin, txtFltDurMax, txtFltCapMin, txtFltCapMax,
                                      txtFltWinMin, txtFltWinMax, txtFltPnlMin, txtFltPnlMax,
                                      txtFltOpsMin, txtFltOpsMax, txtFltSharpeMin, txtFltDDMax}
            tb.Text = ""
        Next
        If cmbFltActivo.Items.Count > 0 Then cmbFltActivo.SelectedIndex = 0
        If cmbFltTf.Items.Count > 0 Then cmbFltTf.SelectedIndex = 0
        If cmbFltEstado.Items.Count > 0 Then cmbFltEstado.SelectedIndex = 0
        chkFltSoloGanadoras.IsChecked = False
        chkFltSoloPersistidas.IsChecked = False
        chkFltConOps.IsChecked = False
        _filtrosListos = True
        AplicarFiltro()
        txtStatus.Text = "Filtros avanzados limpiados."
    End Sub

    Private Sub btnFiltro_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        _filtroActual = btn.Tag?.ToString()
        ' Estilizar el activo
        Dim primary = CType(Me.Resources("BtnPrimary"), Style)
        Dim ghost = CType(Me.Resources("BtnGhost"), Style)
        For Each b In {btnFiltroTodos, btnFiltroGanadoras, btnFiltroPerdedoras, btnFiltroTop10}
            b.Style = If(b Is btn, primary, ghost)
        Next
        AplicarFiltro()
    End Sub

    Private Sub ActualizarKpis()
        Dim filas = _todasLasFilas
        Dim total = filas.Count
        kCombi.Text = total.ToString("N0")
        kCombiSub.Text = If(total > 0, "ejecutadas", "sin ejecutar")
        Dim ganadoras = filas.AsEnumerable().Count(Function(r) r.EsGanadora)
        kWin.Text = ganadoras.ToString()
        kOpsT.Text = filas.Sum(Function(r) r.Ops).ToString("N0")
        kOpsW.Text = If(total > 0, filas.Average(Function(r) r.OpsSem).ToString("F1"), "—")
        Dim pnlNeto = filas.Sum(Function(r) r.Pnl)
        kPnl.Text = pnlNeto.ToString("+0.00;-0.00;0.00")
        kPnl.Foreground = If(pnlNeto >= 0D,
                            New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A)),
                            New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A)))
        Dim mejor = filas.OrderByDescending(Function(r) r.Pnl).FirstOrDefault()
        If mejor IsNot Nothing Then
            kBest.Text = String.Format("{0} · {1}/{2}", mejor.Estrategia, mejor.Activo, mejor.Timeframe)
            kBestSub.Text = String.Format("PnL: {0}  ·  Win: {1}", mejor.PnlStr, mejor.WinStr)
        End If
    End Sub

    ' ── DRAWER ──────────────────────────────────────────────────────

    ''' <summary>
    ''' Click en el boton "👁 Ver" de una fila → abre una ventana INDEPENDIENTE
    ''' nueva con el detalle de operaciones de esa sesion. Permite tener varias
    ''' abiertas a la vez para comparar visualmente.
    ''' </summary>
    Private Sub btnVerOpsRow_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim row = TryCast(btn.DataContext, HeavyResultRow)
        If row Is Nothing Then Return
        If row.SesionId <= 0 Then
            txtStatus.Text = "Esta fila aun no esta persistida en BD."
            Return
        End If
        ' Ventana 100% independiente — sin Owner, sin ShowDialog, taskbar propia.
        ' El usuario puede abrir tantas como quiera y dejarlas abiertas.
        Dim w As New HeavyOperacionesWindow()
        w.Show()
        Dim _t = w.CargarAsync(row.SesionId)
        txtStatus.Text = "Abriendo ventana de operaciones para sesion #" & row.SesionId
    End Sub

    Private Sub AbrirDrawer(job As HeavyTestJob)
        Dim drawer As New HeavyDetailDrawer()
        AddHandler drawer.Cerrar, AddressOf CerrarDrawer
        drawer.Cargar(job)
        drawerContent.Content = drawer
        overlayDrawer.Visibility = Visibility.Visible
    End Sub

    Private Sub CerrarDrawer()
        overlayDrawer.Visibility = Visibility.Collapsed
        drawerContent.Content = Nothing
        dgResultados.SelectedItem = Nothing
    End Sub

    Private Sub overlayDrawer_BgClick(sender As Object, e As Input.MouseButtonEventArgs)
        ' click en background (fuera del drawer) cierra
        CerrarDrawer()
    End Sub

    Private Sub OverlayDrawerStop_Click(sender As Object, e As Input.MouseButtonEventArgs)
        ' click dentro del drawer no debe propagar al overlay
        e.Handled = True
    End Sub

    ' ── CHECKBOX SELECCION + ACCIONES MASIVAS ────────────────────────

    Private Sub rowChk_Toggled(sender As Object, e As RoutedEventArgs)
        ActualizarLblSeleccionados()
    End Sub

    Private Sub chkAllRows_Toggled(sender As Object, e As RoutedEventArgs)
        Dim cb = TryCast(sender, CheckBox)
        If cb Is Nothing Then Return
        MarcarTodasVisibles(cb.IsChecked = True)
    End Sub

    Private Sub btnSeleccionarTodas_Click(sender As Object, e As RoutedEventArgs)
        MarcarTodasVisibles(True)
        txtStatus.Text = "Todas las filas visibles seleccionadas."
    End Sub

    Private Sub btnDeseleccionarTodas_Click(sender As Object, e As RoutedEventArgs)
        ' Desmarca TODAS, no solo las visibles, asi limpia tambien las que el filtro oculto
        For Each r In _todasLasFilas
            r.Selected = False
        Next
        ActualizarLblSeleccionados()
        txtStatus.Text = "Selección limpiada."
    End Sub

    ''' <summary>Marca o desmarca todas las filas visibles segun el filtro actual del grid.</summary>
    Private Sub MarcarTodasVisibles(marcar As Boolean)
        Dim items = TryCast(dgResultados.ItemsSource, IEnumerable(Of HeavyResultRow))
        If items Is Nothing Then Return
        For Each r In items
            r.Selected = marcar
        Next
        ActualizarLblSeleccionados()
    End Sub

    Private Sub ActualizarLblSeleccionados()
        Dim n = _todasLasFilas.AsEnumerable().Count(Function(x) x.Selected)
        lblSeleccionados.Text = n & " sel"
    End Sub

    ''' <summary>
    ''' Boton de la barra de filtros: abre una ventana INDEPENDIENTE por CADA fila
    ''' marcada con checkbox. Permite comparar varias estrategias visualmente.
    ''' (Limitado a 6 con confirmacion para no abrir cientos por error.)
    ''' Si no hay nada marcado, instruye al usuario que use el boton de la fila.
    ''' </summary>
    Private Sub btnVerOperaciones_Click(sender As Object, e As RoutedEventArgs)
        Dim seleccionadas = _todasLasFilas.Where(Function(r) r.Selected AndAlso r.SesionId > 0).ToList()
        If seleccionadas.Count = 0 Then
            txtStatus.Text = "Marca una o varias filas con el checkbox, o usa el boton 👁 Ver de cada fila."
            Return
        End If
        If seleccionadas.Count > 6 Then
            Dim confirm = MessageBox.Show(
                String.Format("Vas a abrir {0} ventanas (una por sesion). ¿Continuar?", seleccionadas.Count),
                "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            If confirm <> MessageBoxResult.Yes Then Return
        End If
        For Each fila In seleccionadas
            Dim w As New HeavyOperacionesWindow()
            w.Show()
            Dim _t = w.CargarAsync(fila.SesionId)
        Next
        txtStatus.Text = String.Format("Abriendo {0} ventana(s) de operaciones.", seleccionadas.Count)
    End Sub

    Private Async Sub btnEliminarSeleccion_Click(sender As Object, e As RoutedEventArgs)
        Dim seleccionadas = _todasLasFilas.Where(Function(r) r.Selected).ToList()
        If seleccionadas.Count = 0 Then
            txtStatus.Text = "No hay filas marcadas para eliminar."
            Return
        End If
        Dim conSesion = seleccionadas.Where(Function(r) r.SesionId > 0).ToList()
        Dim sinSesion = seleccionadas.Count - conSesion.Count

        Dim msg = String.Format("Eliminar {0} sesion(es) de la BD junto con todas sus operaciones?",
                                 conSesion.Count)
        If sinSesion > 0 Then
            msg &= Environment.NewLine & String.Format("({0} fila(s) sin id de BD seran solo removidas del grid.)", sinSesion)
        End If
        msg &= Environment.NewLine & Environment.NewLine & "Esta accion no se puede deshacer."

        Dim confirm = MessageBox.Show(msg, "Confirmar eliminacion",
                                      MessageBoxButton.YesNo, MessageBoxImage.Warning)
        If confirm <> MessageBoxResult.Yes Then Return

        btnEliminarSeleccion.IsEnabled = False
        Dim eliminadas As Integer = 0
        Dim opsTotales As Integer = 0
        Dim errores As Integer = 0
        Dim errorMsgs As New List(Of String)()
        Dim filasOk As New List(Of HeavyResultRow)()

        For Each filaBd In conSesion
            Try
                Dim res = Await _repo.EliminarSesionAsync(filaBd.SesionId)
                eliminadas += 1
                opsTotales += res.OpsBorradas
                filasOk.Add(filaBd)
                AppLogger.Info(String.Format("[Heavy] Sesion #{0} borrada con {1} operaciones.",
                                              filaBd.SesionId, res.OpsBorradas))
            Catch ex As Exception
                errores += 1
                errorMsgs.Add("#" & filaBd.SesionId & ": " & ex.Message)
                AppLogger.Warn("[Heavy] Eliminar sesion #" & filaBd.SesionId & " fallo: " & ex.Message)
            End Try
        Next

        ' Solo quitar del grid las que efectivamente se borraron (ok) + las locales sin id
        For Each f In filasOk
            _todasLasFilas.Remove(f)
        Next
        For Each f In seleccionadas.Where(Function(x) x.SesionId <= 0).ToList()
            _todasLasFilas.Remove(f)
        Next

        ReindexarYRefrescar()
        ActualizarKpis()
        ActualizarLblSeleccionados()
        btnEliminarSeleccion.IsEnabled = True

        If errores > 0 Then
            txtStatus.Text = String.Format(
                "{0} sesion(es) borradas ({1:N0} ops). {2} con error: {3}",
                eliminadas, opsTotales, errores, String.Join(" | ", errorMsgs.Take(2)))
        Else
            txtStatus.Text = String.Format(
                "✓ {0} sesion(es) y {1:N0} operaciones eliminadas de BD ({2} locales sin id removidas).",
                eliminadas, opsTotales, sinSesion)
        End If
    End Sub

    ''' <summary>
    ''' Pone al aire en VIVO las combinaciones marcadas con checkbox, usando el
    ''' LiveStrategiesController (singleton del modulo Servidor). Cada fila se
    ''' convierte en un motor live con su config exacta (estrategia, activo, TF,
    ''' confianza, payout, duracion) y el monto indicado en txtMontoLive.
    ''' Flujo sugerido: filtro "Top 10" → "Seleccionar todas" → este boton.
    ''' Las tarjetas aparecen en la ventana Servidor Live y, si el cliente
    ''' SignalR esta conectado, el estado se reporta al servidor remoto.
    ''' </summary>
    Private Async Sub btnEnviarLive_Click(sender As Object, e As RoutedEventArgs)
        Dim seleccionadas = _todasLasFilas.Where(Function(r) r.Selected).ToList()
        If seleccionadas.Count = 0 Then
            txtStatus.Text = "Marca con checkbox las combinaciones a poner al aire (tip: Top 10 → Seleccionar todas)."
            Return
        End If

        Dim monto As Decimal = 1D
        Decimal.TryParse(txtMontoLive.Text.Trim().Replace(",", "."),
                          Globalization.NumberStyles.Any,
                          Globalization.CultureInfo.InvariantCulture, monto)
        If monto <= 0D Then monto = 1D

        ' Avisar si alguna seleccionada no es ganadora segun el backtest
        Dim perdedoras = seleccionadas.AsEnumerable().Count(Function(r) Not r.EsGanadora)
        Dim resumen = String.Join(Environment.NewLine,
            seleccionadas.Take(8).Select(Function(r) String.Format(
                "  - {0} | {1}/{2} conf:{3}% pay:{4}% dur:{5}v (win {6:F1}%)",
                r.Estrategia, r.Activo, r.Timeframe, r.Conf, r.Pay, r.Dur, r.Win)))
        If seleccionadas.Count > 8 Then
            resumen &= Environment.NewLine & String.Format("  ... y {0} mas", seleccionadas.Count - 8)
        End If
        Dim advertencia = If(perdedoras > 0,
            Environment.NewLine & Environment.NewLine &
            String.Format("ATENCION: {0} de las marcadas NO es ganadora en backtest.", perdedoras), "")
        Dim sinVentaja = seleccionadas.AsEnumerable().Count(Function(r) r.Veredicto <> "VENTAJA ESTADISTICA")
        If sinVentaja > 0 Then
            advertencia &= Environment.NewLine & Environment.NewLine &
                String.Format("ATENCION: {0} de las marcadas NO tiene ventaja demostrada en el periodo ciego (columna Veredicto ciego). " &
                              "Lo recomendable es probarlas antes con el boton Validar.", sinVentaja)
        End If

        Dim msg = String.Format(
            "¿Poner al aire {0} estrategia(s) EN VIVO con ${1:F2} por operacion?{2}{2}{3}{4}",
            seleccionadas.Count, monto, Environment.NewLine, resumen, advertencia)
        Dim confirm = MessageBox.Show(msg, "Enviar a Servidor Live",
                                      MessageBoxButton.YesNo, MessageBoxImage.Question)
        If confirm <> MessageBoxResult.Yes Then Return

        btnEnviarLive.IsEnabled = False
        Dim okCount = 0
        Dim errCount = 0

        ' Si el cliente SignalR esta conectado, la instruccion viaja A TRAVES del
        ' servidor: SendInstruction(hub) → el server la enruta de vuelta a este
        ' cliente → CommandRouter la ejecuta y responde. Asi el servidor tiene
        ' registro y visibilidad de cada estrategia puesta al aire.
        ' Si NO esta conectado, fallback: iniciar el motor localmente.
        Dim signalR = SignalRClientService.GetInstance()
        Dim viaServidor = signalR.EstaConectado
        Dim ctrl = LiveStrategiesController.GetInstance()

        For Each fila In seleccionadas
            Try
                If viaServidor Then
                    Dim payload As New JObject From {
                        {"estrategia", fila.Estrategia},
                        {"symbol", fila.Activo},
                        {"timeframe", fila.Timeframe},
                        {"confianza", fila.Conf},
                        {"payout", fila.Pay},
                        {"duracion", fila.Dur},
                        {"monto", monto},
                        {"autonomo", True}
                    }
                    Dim okSend = Await signalR.EnviarInstruccionAsync(
                        signalR.ClientId, "estrategia.iniciar",
                        payload.ToString(Newtonsoft.Json.Formatting.None))
                    If okSend Then
                        okCount += 1
                        AppLogger.Info(String.Format("[Heavy→Live] Instruccion via servidor: {0} {1}/{2} conf:{3} monto:${4:F2}",
                                                      fila.Estrategia, fila.Activo, fila.Timeframe, fila.Conf, monto))
                    Else
                        errCount += 1
                        AppLogger.Warn("[Heavy→Live] SendInstruction fallo para " & fila.Estrategia)
                    End If
                Else
                    Dim sid = Await ctrl.IniciarAsync(fila.Estrategia, fila.Activo, fila.Timeframe,
                                                       fila.Conf, fila.Pay, fila.Dur, monto)
                    ' El modo autonomo la reanudara si el PC se reinicia
                    Await New LiveTestRepository().MarcarReanudacionAutomaticaAsync(sid, True)
                    okCount += 1
                    AppLogger.Info(String.Format("[Heavy→Live] Sesion #{0} (local): {1} {2}/{3} conf:{4} monto:${5:F2}",
                                                  sid, fila.Estrategia, fila.Activo, fila.Timeframe, fila.Conf, monto))
                End If
            Catch ex As Exception
                errCount += 1
                AppLogger.Warn(String.Format("[Heavy→Live] Error iniciando {0} {1}/{2}: {3}",
                                              fila.Estrategia, fila.Activo, fila.Timeframe, ex.Message))
            End Try
        Next
        btnEnviarLive.IsEnabled = True

        Dim modo = If(viaServidor, "enviadas via servidor SignalR (" & signalR.ServerUrl & ")", "iniciadas localmente (SignalR desconectado)")
        If errCount = 0 Then
            txtStatus.Text = String.Format("📡 {0} estrategia(s) {1}. Ver tarjetas en Servidor Live.", okCount, modo)
        Else
            txtStatus.Text = String.Format("📡 {0} ok, {1} con error — {2}. Ver app.log.", okCount, errCount, modo)
        End If
    End Sub

    ' ── VALIDACION COMPLETA ─────────────────────────────────────────

    ''' <summary>
    ''' Prueba completa de la primera combinacion marcada: diseno frente a ciego, walk-forward,
    ''' Monte Carlo, ruido en precios y la misma regla en los demas mercados de esa temporalidad.
    ''' El informe se muestra en una ventana aparte y se va completando por pasos.
    ''' </summary>
    Private Async Sub btnValidar_Click(sender As Object, e As RoutedEventArgs)
        Dim fila = _todasLasFilas.FirstOrDefault(Function(r) r.Selected)
        If fila Is Nothing Then
            txtStatus.Text = "Marca con checkbox la combinacion a validar."
            Return
        End If
        Dim corte = CorteEpoch()
        If corte = 0 Then
            txtStatus.Text = "Indica el inicio del periodo ciego (AAAA-MM-DD) para validar."
            Return
        End If
        EstrategiasDinamicasRegistry.Inicializar()
        Dim est = EstrategiasDinamicasRegistry.GetByNombre(fila.Estrategia)
        If est Is Nothing Then
            txtStatus.Text = "Estrategia no encontrada: " & fila.Estrategia
            Return
        End If

        Dim caja As New TextBox With {
            .IsReadOnly = True, .FontFamily = New FontFamily("Consolas"), .FontSize = 12,
            .Background = New SolidColorBrush(Color.FromRgb(&H12, &H17, &H23)),
            .Foreground = New SolidColorBrush(Color.FromRgb(&HE7, &HEC, &HF5)),
            .BorderThickness = New Thickness(0), .Padding = New Thickness(12),
            .TextWrapping = TextWrapping.Wrap, .VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }
        Dim ventana As New Window With {
            .Title = String.Format("Validacion: {0} {1} {2}", fila.Estrategia, fila.Activo, fila.Timeframe),
            .Width = 900, .Height = 620, .Owner = Me, .Content = caja,
            .Background = caja.Background, .WindowStartupLocation = WindowStartupLocation.CenterOwner
        }
        ventana.Show()
        btnValidar.IsEnabled = False
        If _todasLasFilas.Where(Function(r) r.Selected).Count() > 1 Then
            txtStatus.Text = "Se valida solo la primera combinacion marcada."
        End If
        Try
            caja.Text = "1/3 Prueba ciega..."
            Dim validar = Await ValidacionEstrategia.ValidarAsync(est, fila.Activo, fila.Timeframe, fila.Dur, fila.Conf, fila.Pay, corte)
            caja.Text = ValidacionCompleta.InformeTexto(validar, Nothing, Nothing) & Environment.NewLine & "2/3 Walk-forward, Monte Carlo y ruido (puede tardar unos minutos)..."
            Dim robustez = Await ValidacionCompleta.RobustezAsync(est, fila.Activo, fila.Timeframe, fila.Dur, fila.Conf, fila.Pay, corte)
            caja.Text = ValidacionCompleta.InformeTexto(validar, robustez, Nothing) & Environment.NewLine & "3/3 Otros mercados..."
            Dim series = Await New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString()).ListarSeriesAsync("Deriv")
            Dim mercados = series.Where(Function(s) s.Item2 = fila.Timeframe).Select(Function(s) Tuple.Create(s.Item1, s.Item2)).ToList()
            Dim multi = Await ValidacionCompleta.MultiMercadoAsync(est, mercados, fila.Dur, fila.Conf, fila.Pay, corte)
            caja.Text = ValidacionCompleta.InformeTexto(validar, robustez, multi)
            txtStatus.Text = "Validacion terminada: " & ValidacionCompleta.Conclusion(validar, robustez)
        Catch ex As Exception
            caja.Text &= Environment.NewLine & Environment.NewLine & "Error: " & ex.Message
            txtStatus.Text = "Validacion: " & ex.Message
        Finally
            btnValidar.IsEnabled = True
        End Try
    End Sub

    ' ── EXPORT CSV ──────────────────────────────────────────────────

    Private Sub btnExportarCsv_Click(sender As Object, e As RoutedEventArgs)
        If _todasLasFilas.Count = 0 Then
            txtStatus.Text = "No hay resultados para exportar."
            Return
        End If
        Try
            Dim baseDir = AppDomain.CurrentDomain.BaseDirectory
            Dim outDir = Path.Combine(baseDir, "backups")
            If Not Directory.Exists(outDir) Then Directory.CreateDirectory(outDir)
            Dim ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            Dim archivo = Path.Combine(outDir, "heavy_results_" & ts & ".csv")

            Using sw As New StreamWriter(archivo, False, System.Text.Encoding.UTF8)
                sw.WriteLine("Rank,Estrategia,Activo,TF,Conf%,Pay%,Dur,Cap,Ops,OpsSem,Win%,PnL,DD%,Sharpe,Estado")
                For Each r In _todasLasFilas.OrderByDescending(Function(x) x.Pnl)
                    sw.WriteLine(String.Join(",", New String() {
                        r.Rank.ToString(), Esc(r.Estrategia), Esc(r.Activo), Esc(r.Timeframe),
                        r.Conf.ToString(), r.Pay.ToString("F2"), r.Dur.ToString(), r.Cap.ToString("F0"),
                        r.Ops.ToString(), r.OpsSem.ToString("F2"), r.Win.ToString("F2"),
                        r.Pnl.ToString("F2"), r.DD.ToString("F2"), r.Sharpe.ToString("F2"),
                        Esc(r.Estado)
                    }))
                Next
            End Using
            txtStatus.Text = "CSV exportado: " & archivo
        Catch ex As Exception
            txtStatus.Text = "Error exportando: " & ex.Message
        End Try
    End Sub

    Private Function Esc(s As String) As String
        If s Is Nothing Then Return ""
        If s.Contains(",") OrElse s.Contains("""") Then
            Return """" & s.Replace("""", """""") & """"
        End If
        Return s
    End Function

    ' ── SERVIDOR API EMBEBIDO ───────────────────────────────────────
    ' Permite controlar el Heavy Tester desde curl/Claude por HTTP.
    ' Coexiste con TradingApiServer (puerto 8765); por defecto este usa 8766.

    Private Sub btnApiServer_Click(sender As Object, e As RoutedEventArgs)
        If _apiServer IsNot Nothing AndAlso _apiServer.IsRunning Then
            Try
                _apiServer.Stop()
            Catch
            End Try
            _apiServer = Nothing
            btnApiServer.Content = "▶ Iniciar"
            btnApiServer.Background = New SolidColorBrush(Color.FromRgb(&HD, &H2A, &H30))
            btnApiServer.Foreground = New SolidColorBrush(Color.FromRgb(&H5E, &HE0, &HD0))
            btnApiServer.BorderBrush = New SolidColorBrush(Color.FromRgb(&H5E, &HE0, &HD0))
            pillApi.Text = "API: stopped"
            txtStatus.Text = "Servidor API detenido."
            Return
        End If

        Dim port As Integer = 8766
        Integer.TryParse(txtApiPort.Text.Trim(), port)
        If port < 1024 OrElse port > 65535 Then port = 8766

        Try
            _apiServer = New HeavyApiServer(port) With {
                .Queue = _queue,
                .Repo = _repo,
                .ConnectionString = ConfigManager.LoadDbConfig().GetConnectionString(),
                .UiDispatcher = Me.Dispatcher,
                .LogAction = Sub(m) Dispatcher.BeginInvoke(Sub() txtStatus.Text = m, DispatcherPriority.Background),
                .StartEjecucion = AddressOf ApiStartEjecucion
            }
            _apiServer.Start()
            btnApiServer.Content = "■ Detener"
            btnApiServer.Background = New SolidColorBrush(Color.FromRgb(&H3A, &H22, &H30))
            btnApiServer.Foreground = New SolidColorBrush(Color.FromRgb(&HFF, &HFF, &HFF))
            btnApiServer.BorderBrush = New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            pillApi.Text = String.Format("API: http://localhost:{0}", port)
            txtStatus.Text = String.Format("Servidor Heavy API iniciado en puerto {0}. GET /api/heavy/help", port)
        Catch ex As Exception
            MessageBox.Show("No se pudo iniciar el servidor: " & ex.Message,
                            "Heavy API", MessageBoxButton.OK, MessageBoxImage.Error)
            _apiServer = Nothing
        End Try
    End Sub

    ''' <summary>
    ''' Adaptador que el servidor llama via callback para arrancar la ejecucion.
    ''' Replica la logica de btnEjecutar_Click (limpia resultados previos, levanta
    ''' los toggles de la UI y dispara la cola), pero sin requerir click en el boton.
    ''' </summary>
    Private Async Function ApiStartEjecucion(concurrencia As Integer) As Task
        If _queue.EnCurso Then Return
        If concurrencia < 1 Then concurrencia = 1
        If concurrencia > 8 Then concurrencia = 8

        ' Limpiar resultados previos en memoria (no toca BD)
        _todasLasFilas.RemoveAll(Function(r) r.JobRef IsNot Nothing AndAlso r.SesionId = 0)

        pillEngine.Text = "Engine v0.9 · running (api)"
        txtStatus.Text = String.Format("[API] Ejecutando {0} jobs (concurrencia {1})...", _queue.Jobs.Count, concurrencia)
        btnEjecutar.IsEnabled = False
        btnLimpiar.IsEnabled = False
        btnGenerar.IsEnabled = False
        btnDetener.IsEnabled = True
        Try
            Await _queue.EjecutarTodasAsync(concurrencia)
        Finally
            btnEjecutar.IsEnabled = True
            btnLimpiar.IsEnabled = True
            btnGenerar.IsEnabled = True
            btnDetener.IsEnabled = False
            pillEngine.Text = "Engine v0.9 · idle"
        End Try
    End Function

End Class
