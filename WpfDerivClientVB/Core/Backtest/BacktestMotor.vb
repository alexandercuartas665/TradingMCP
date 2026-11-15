Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

    ''' <summary>
    ''' Snapshot de progreso del backtest emitido cada N velas. Permite a la UI
    ''' mostrar contadores en tiempo real (velas analizadas, ganadas/perdidas, etc.).
    ''' </summary>
    Public Structure BacktestProgresoMetricas
        Public VelasAnalizadas As Integer
        Public TotalVelas As Integer
        Public TotalSenales As Integer
        Public TotalOps As Integer
        Public Ganadas As Integer
        Public Perdidas As Integer
        Public PnlActual As Decimal
    End Structure

    ''' <summary>
    ''' Motor de backtesting: ejecuta una estrategia sobre datos historicos,
    ''' simula multiples cuentas en paralelo y devuelve metricas completas.
    '''
    ''' Uso basico:
    '''   Dim resultado = Await New BacktestMotor().EjecutarAsync(config, New EstrategiaEmaRsi())
    ''' </summary>
    Public Class BacktestMotor

        ''' <summary>
        ''' Ejecuta el backtest completo y guarda los resultados en PostgreSQL.
        ''' </summary>
        ''' <param name="config">Configuracion de la sesion (symbol, fechas, cuentas, etc.)</param>
        ''' <param name="estrategia">Estrategia a probar (EstrategiaEmaRsi, EstrategiaSeykota, etc.)</param>
        ''' <param name="progreso">Progreso textual opcional para reportar estado a la UI.</param>
        ''' <param name="progresoMetricas">Progreso estructurado opcional con metricas en tiempo real (velas + ganadas/perdidas).</param>
        ''' <param name="ct">Token de cancelacion: lanza OperationCanceledException a mitad del loop de velas (no se guarda nada en BD).</param>
        Public Async Function EjecutarAsync(config As BacktestConfig,
                                            estrategia As IEstrategia,
                                            Optional progreso As IProgress(Of String) = Nothing,
                                            Optional progresoMetricas As IProgress(Of BacktestProgresoMetricas) = Nothing,
                                            Optional ct As CancellationToken = Nothing) As Task(Of BacktestResultado)

            ' -- FASE 0: Carga de velas ------------------------------------------
            Reportar(progreso, "Cargando velas historicas...")
            Dim repo = New CandleRepository(ConfigManager.LoadDbConfig().GetConnectionString())
            Dim todasLasVelas = Await repo.GetCandlesRangoAsync(
                config.Platform, config.Symbol, config.Timeframe,
                config.DesdeEpoch, config.HastaEpoch)

            Dim minimoRequerido = config.VentanaIndicadores + config.DuracionVelas
            If todasLasVelas.Count < minimoRequerido Then
                Throw New Exception(String.Format(
                    "Velas insuficientes: {0} disponibles, se necesitan al menos {1} " &
                    "(VentanaIndicadores={2} + DuracionVelas={3}).",
                    todasLasVelas.Count, minimoRequerido,
                    config.VentanaIndicadores, config.DuracionVelas))
            End If

            Reportar(progreso, String.Format("{0} velas cargadas. Iniciando simulacion...", todasLasVelas.Count))

            ' -- FASE 1: Simulacion (funcion pura, sin BD) ---------------------------
            Dim sim = Simular(todasLasVelas, estrategia, config, progreso, progresoMetricas, ct)
            Dim cuentas = sim.CuentasFinales
            Dim operaciones = sim.Operaciones
            Dim totalSenales = sim.TotalSenales
            If sim.DescartadasPorHueco > 0 OrElse sim.OmitidasPorPosicionAbierta > 0 Then
                Reportar(progreso, String.Format("Senales descartadas: {0} por hueco de datos, {1} por posicion abierta.",
                                                 sim.DescartadasPorHueco, sim.OmitidasPorPosicionAbierta))
            End If

            ' -- FASE 2: Metricas ------------------------------------------------
            Reportar(progreso, "Calculando metricas...")
            Dim sesion = CalcularMetricas(config, estrategia.Nombre, cuentas, operaciones, totalSenales)

            ' -- FASE 3: Log de perdidas ------------------------------------------
            For Each op In operaciones
                If op.Estado = "Perdido" Then
                    AppLogger.Warn(String.Format(
                        "[BACKTEST-PERDIDA] Sesion={0} Cuenta={1} Tipo={2} Epoch={3} " &
                        "Entrada={4:F5} Salida={5:F5} Confianza={6} Motivo={7} Datos={8}",
                        sesion.Estrategia, op.CuentaNombre, op.Tipo, op.EpochEntrada,
                        op.PrecioEntrada, op.PrecioSalida, op.Confianza,
                        op.MotivoSenal, op.DatosIndicadores))
                End If
            Next

            ' -- FASE 4: Guardar en BD -------------------------------------------
            Reportar(progreso, "Guardando en base de datos...")
            Dim btRepo As New BacktestRepository()
            Await btRepo.EnsureTablesExistAsync()
            sim.Sesion = sesion
            Dim sesionId = Await btRepo.GuardarSesionConOperacionesAsync(sim)
            sim.Sesion.Id = sesionId

            Reportar(progreso, String.Format("Backtest completado. Sesion ID={0}. {1} operaciones, WinRate={2:P1}",
                sesionId, sesion.TotalOperaciones, sesion.WinRate))
            Return sim
        End Function

        ''' <summary>Segundos por vela del timeframe ("1m", "15m", "1h", "4h", "1d"...). 0 si no se reconoce.</summary>
        Public Shared Function SegundosTimeframe(timeframe As String) As Integer
            If String.IsNullOrWhiteSpace(timeframe) Then Return 0
            Dim m = System.Text.RegularExpressions.Regex.Match(timeframe.Trim().ToLowerInvariant(), "^(\d+)\s*([smhd])$")
            If Not m.Success Then Return 0
            Dim n = Integer.Parse(m.Groups(1).Value)
            Select Case m.Groups(2).Value
                Case "s" : Return n
                Case "m" : Return n * 60
                Case "h" : Return n * 3600
                Case "d" : Return n * 86400
            End Select
            Return 0
        End Function

        ''' <summary>
        ''' Simulacion del backtest sobre velas ya cargadas (sin BD: se puede probar).
        '''
        ''' Reglas, alineadas con lo que se opera en Deriv:
        '''  - La senal se evalua con la vela i ya cerrada y se entra al cierre de i.
        '''  - El contrato dura DuracionVelas x timeframe: sale al cierre de la vela que
        '''    termina exactamente ese tiempo despues (i + N, si no faltan velas).
        '''  - Si en ese tramo faltan velas (fin de semana / hueco de datos) la operacion
        '''    se DESCARTA: en Deriv ese contrato habria durado otra cosa.
        '''  - Empate (salida = entrada) es perdida, como en Deriv rise/fall.
        '''  - UnaPosicionALaVez: no se abre otra operacion mientras una siga en curso.
        ''' </summary>
        Public Shared Function Simular(todasLasVelas As List(Of CandleModel),
                                       estrategia As IEstrategia,
                                       config As BacktestConfig,
                                       Optional progreso As IProgress(Of String) = Nothing,
                                       Optional progresoMetricas As IProgress(Of BacktestProgresoMetricas) = Nothing,
                                       Optional ct As CancellationToken = Nothing) As BacktestResultado

            ' -- Clonar cuentas para no mutar el config original ------------------
            Dim cuentas As New List(Of BacktestCuenta)()
            For Each c In config.Cuentas
                cuentas.Add(New BacktestCuenta With {
                    .NombreCuenta      = c.NombreCuenta,
                    .BalanceInicial    = c.BalanceInicial,
                    .BalanceActual     = c.BalanceInicial,
                    .MontoPorOperacion = c.MontoPorOperacion
                })
            Next

            ' Duracion del contrato en segundos (si el timeframe no se reconoce, se
            ' infiere del espaciado mas comun entre velas consecutivas)
            Dim tfSeg As Long = SegundosTimeframe(config.Timeframe)
            If tfSeg <= 0 AndAlso todasLasVelas.Count > 2 Then
                tfSeg = todasLasVelas.Zip(todasLasVelas.Skip(1), Function(a, b) b.Epoch - a.Epoch).
                        GroupBy(Function(d) d).OrderByDescending(Function(g) g.Count()).First().Key
            End If
            Dim duracionSeg As Long = tfSeg * config.DuracionVelas

            Dim operaciones As New List(Of BacktestOperacion)()
            Dim totalSenales As Integer = 0
            Dim totalOps As Integer = 0
            Dim totalGanadas As Integer = 0
            Dim totalPerdidas As Integer = 0
            Dim pnlAcumulado As Decimal = 0D
            Dim descartadasHueco As Integer = 0
            Dim omitidasPosicion As Integer = 0
            Dim libreDesdeIndice As Integer = -1   ' con UnaPosicionALaVez: indice de la vela de salida en curso

            Dim iMax = todasLasVelas.Count - config.DuracionVelas - 1
            For i = config.VentanaIndicadores - 1 To iMax

                If i Mod 500 = 0 Then
                    ' Punto de cancelacion: aborta el backtest sin tocar BD.
                    ct.ThrowIfCancellationRequested()
                    Reportar(progreso, String.Format("Procesando vela {0}/{1}...", i, todasLasVelas.Count))
                    ReportarMetricas(progresoMetricas, New BacktestProgresoMetricas With {
                        .VelasAnalizadas = i,
                        .TotalVelas = todasLasVelas.Count,
                        .TotalSenales = totalSenales,
                        .TotalOps = totalOps,
                        .Ganadas = totalGanadas,
                        .Perdidas = totalPerdidas,
                        .PnlActual = pnlAcumulado
                    })
                End If

                ' Ventana de historia para la estrategia (ultimas VentanaIndicadores velas)
                Dim inicio = i - config.VentanaIndicadores + 1
                Dim ventana = todasLasVelas.GetRange(inicio, config.VentanaIndicadores)

                Dim resultado = estrategia.Analizar(ventana)

                If resultado.Senal = "NEUTRAL" Then Continue For
                totalSenales += 1
                If resultado.Confianza < config.MinConfianza Then Continue For

                ' Una sola posicion a la vez: ignorar senales mientras otra este en curso
                If config.UnaPosicionALaVez AndAlso i < libreDesdeIndice Then
                    omitidasPosicion += 1
                    Continue For
                End If

                ' Vela de entrada y de salida POR TIEMPO: la de salida debe cerrar exactamente
                ' DuracionVelas x timeframe despues. Si faltan velas en el tramo, se descarta.
                Dim velaEntrada = todasLasVelas(i)
                Dim velaSalida  = todasLasVelas(i + config.DuracionVelas)
                If tfSeg > 0 AndAlso velaSalida.Epoch - velaEntrada.Epoch <> duracionSeg Then
                    descartadasHueco += 1
                    Continue For
                End If
                If config.UnaPosicionALaVez Then libreDesdeIndice = i + config.DuracionVelas

                ' Logica de opciones binarias
                Dim esPrecioSubio = velaSalida.ClosePrice > velaEntrada.ClosePrice
                Dim estadoTrade As String
                If resultado.Senal = "CALL" Then
                    estadoTrade = If(esPrecioSubio, "Ganado", "Perdido")
                Else   ' PUT
                    estadoTrade = If(Not esPrecioSubio, "Ganado", "Perdido")
                End If
                ' Precio identico = Perdido (sin movimiento, no se gana en binarias)
                If velaSalida.ClosePrice = velaEntrada.ClosePrice Then estadoTrade = "Perdido"

                ' Serializar datos de indicadores (siempre)
                Dim datosJson = JsonConvert.SerializeObject(resultado.Datos)

                ' Serializar contexto de velas (solo en perdidas)
                Dim contextoJson As String = ""
                If estadoTrade = "Perdido" Then
                    Dim ctxInicio = Math.Max(0, i - config.GuardarContextoVelas + 1)
                    Dim ctxCount  = i - ctxInicio + 1
                    Dim velasCtx  = todasLasVelas.GetRange(ctxInicio, ctxCount)
                    contextoJson  = SerializarVelasContexto(velasCtx)
                End If

                ' Simular en todas las cuentas
                For Each cuenta In cuentas
                    Dim pnl As Decimal
                    If estadoTrade = "Ganado" Then
                        pnl = cuenta.MontoPorOperacion * config.PayoutPct / 100D
                    Else
                        pnl = -cuenta.MontoPorOperacion
                    End If

                    cuenta.BalanceActual += pnl
                    If estadoTrade = "Ganado" Then
                        cuenta.NGanadas += 1
                        totalGanadas += 1
                    Else
                        cuenta.NPerdidas += 1
                        totalPerdidas += 1
                    End If
                    pnlAcumulado += pnl

                    operaciones.Add(New BacktestOperacion With {
                        .CuentaNombre     = cuenta.NombreCuenta,
                        .Tipo             = resultado.Senal,
                        .EpochEntrada     = velaEntrada.Epoch,
                        .PrecioEntrada    = velaEntrada.ClosePrice,
                        .EpochSalida      = velaSalida.Epoch,
                        .PrecioSalida     = velaSalida.ClosePrice,
                        .Monto            = cuenta.MontoPorOperacion,
                        .PayoutPct        = config.PayoutPct,
                        .Estado           = estadoTrade,
                        .ProfitLoss       = pnl,
                        .BalancePost      = cuenta.BalanceActual,
                        .Confianza        = resultado.Confianza,
                        .MotivoSenal      = resultado.Motivo,
                        .DatosIndicadores = datosJson,
                        .ContextoVelas    = contextoJson
                    })
                    totalOps += 1
                Next
            Next

            ' Reporte final 100% de velas con metricas finales del bucle
            ReportarMetricas(progresoMetricas, New BacktestProgresoMetricas With {
                .VelasAnalizadas = todasLasVelas.Count,
                .TotalVelas = todasLasVelas.Count,
                .TotalSenales = totalSenales,
                .TotalOps = totalOps,
                .Ganadas = totalGanadas,
                .Perdidas = totalPerdidas,
                .PnlActual = pnlAcumulado
            })

            Return New BacktestResultado With {
                .Operaciones = operaciones,
                .CuentasFinales = cuentas,
                .TotalSenales = totalSenales,
                .DescartadasPorHueco = descartadasHueco,
                .OmitidasPorPosicionAbierta = omitidasPosicion,
                .Sesion = CalcularMetricas(config, estrategia.Nombre, cuentas, operaciones, totalSenales)
            }
        End Function

        ' ================================================================
        '  HELPERS PRIVADOS
        ' ================================================================

        Private Shared Function CalcularMetricas(config As BacktestConfig,
                                                  nombreEstrategia As String,
                                                  cuentas As List(Of BacktestCuenta),
                                                  operaciones As List(Of BacktestOperacion),
                                                  totalSenales As Integer) As BacktestSesion

            Dim opsFiltradas = operaciones.FindAll(Function(o) o.CuentaNombre = cuentas(0).NombreCuenta)

            Dim nGanadas  = opsFiltradas.Where(Function(o) o.Estado = "Ganado").Count()
            Dim nPerdidas = opsFiltradas.Where(Function(o) o.Estado = "Perdido").Count()
            Dim totalDivid = nGanadas + nPerdidas
            Dim winRate   = If(totalDivid > 0, CDec(nGanadas) / CDec(totalDivid), 0D)

            Dim totalPnl  = CDec(operaciones.Sum(Function(o) o.ProfitLoss))

            ' Profit factor (ganancias brutas / perdidas brutas) sobre primera cuenta
            Dim ganancias = CDec(opsFiltradas.Where(Function(o) o.ProfitLoss > 0).Sum(Function(o) o.ProfitLoss))
            Dim perdidas  = Math.Abs(CDec(opsFiltradas.Where(Function(o) o.ProfitLoss < 0).Sum(Function(o) o.ProfitLoss)))
            ' Cap a 999.9999 para no desbordar DECIMAL(10,4) en BD cuando no hay perdidas
            Dim profitFactor = If(perdidas > 0, Math.Min(Math.Round(ganancias / perdidas, 4), 999.9999D), 999.9999D)

            ' Max drawdown sobre primera cuenta (pico a valle en porcentaje)
            Dim maxDD = CalcularMaxDrawdown(cuentas(0).BalanceInicial, opsFiltradas)

            Return New BacktestSesion With {
                .Estrategia       = nombreEstrategia,
                .Symbol           = config.Symbol,
                .Timeframe        = config.Timeframe,
                .DesdeEpoch       = config.DesdeEpoch,
                .HastaEpoch       = config.HastaEpoch,
                .DuracionVelas    = config.DuracionVelas,
                .PayoutPct        = config.PayoutPct,
                .MinConfianza     = config.MinConfianza,
                .TotalSenales     = totalSenales,
                .TotalOperaciones = operaciones.Count,
                .WinRate          = winRate,
                .ProfitLossTotal  = totalPnl,
                .MaxDrawdown      = maxDD,
                .ProfitFactor     = profitFactor,
                .Notas            = If(String.IsNullOrEmpty(config.Platform) OrElse config.Platform = FuentesVelas.DERIV, "", "fuente: " & config.Platform),
                .CreatedAt        = DateTime.UtcNow
            }
        End Function

        ''' <summary>
        ''' Calcula el maximo drawdown como porcentaje del balance inicial.
        ''' Algoritmo pico-a-valle sobre la serie de balance_post de la cuenta.
        ''' </summary>
        Private Shared Function CalcularMaxDrawdown(balanceInicial As Decimal,
                                                     ops As List(Of BacktestOperacion)) As Decimal
            If ops.Count = 0 Then Return 0D
            Dim peak = balanceInicial
            Dim maxDD = 0D
            Dim balanceAnterior = balanceInicial
            For Each op In ops
                Dim bal = op.BalancePost
                If bal > peak Then peak = bal
                Dim dd = If(peak > 0, (peak - bal) / peak * 100D, 0D)
                If dd > maxDD Then maxDD = dd
            Next
            Return Math.Round(maxDD, 4)
        End Function

        ''' <summary>
        ''' Serializa una lista de velas a JSON anonimo para guardar como contexto de perdida.
        ''' Formato: [{epoch, open, high, low, close, time}, ...]
        ''' </summary>
        Private Shared Function SerializarVelasContexto(velas As List(Of CandleModel)) As String
            Dim items As New List(Of Object)()
            For Each v In velas
                items.Add(New With {
                    .epoch = v.Epoch,
                    .time  = v.OpenTime,
                    .open  = v.OpenPrice,
                    .high  = v.High,
                    .low   = v.Low,
                    .close = v.ClosePrice
                })
            Next
            Return JsonConvert.SerializeObject(items)
        End Function

        Private Shared Sub ReportarMetricas(progreso As IProgress(Of BacktestProgresoMetricas), m As BacktestProgresoMetricas)
            If progreso IsNot Nothing Then
                Try
                    progreso.Report(m)
                Catch
                End Try
            End If
        End Sub

        Private Shared Sub Reportar(progreso As IProgress(Of String), mensaje As String)
            If progreso IsNot Nothing Then progreso.Report(mensaje)
            Console.WriteLine("[BacktestMotor] " & mensaje)
        End Sub

    End Class

End Namespace
