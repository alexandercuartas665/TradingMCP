Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Newtonsoft.Json
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.Backtesting

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
        ''' <param name="progreso">Progreso opcional para reportar estado a la UI.</param>
        Public Async Function EjecutarAsync(config As BacktestConfig,
                                            estrategia As IEstrategia,
                                            Optional progreso As IProgress(Of String) = Nothing) As Task(Of BacktestResultado)

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

            ' -- FASE 1: Sliding window -------------------------------------------
            Dim operaciones As New List(Of BacktestOperacion)()
            Dim totalSenales As Integer = 0
            Dim totalOps As Integer = 0

            Dim iMax = todasLasVelas.Count - config.DuracionVelas - 1
            For i = config.VentanaIndicadores - 1 To iMax

                If i Mod 500 = 0 Then
                    Reportar(progreso, String.Format("Procesando vela {0}/{1}...", i, todasLasVelas.Count))
                End If

                ' Ventana de historia para la estrategia (ultimas VentanaIndicadores velas)
                Dim inicio = i - config.VentanaIndicadores + 1
                Dim ventana = todasLasVelas.GetRange(inicio, config.VentanaIndicadores)

                Dim resultado = estrategia.Analizar(ventana)

                If resultado.Senal = "NEUTRAL" Then Continue For
                totalSenales += 1
                If resultado.Confianza < config.MinConfianza Then Continue For

                ' Vela de entrada y de salida
                Dim velaEntrada = todasLasVelas(i)
                Dim velaSalida  = todasLasVelas(i + config.DuracionVelas)

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
                    Else
                        cuenta.NPerdidas += 1
                    End If

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

            Dim resultado2 As New BacktestResultado With {
                .Sesion         = sesion,
                .Operaciones    = operaciones,
                .CuentasFinales = cuentas
            }

            Dim sesionId = Await btRepo.GuardarSesionConOperacionesAsync(resultado2)
            resultado2.Sesion.Id = sesionId

            Reportar(progreso, String.Format("Backtest completado. Sesion ID={0}. {1} operaciones, WinRate={2:P1}",
                sesionId, sesion.TotalOperaciones, sesion.WinRate))

            Return resultado2
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
            Dim profitFactor = If(perdidas > 0, Math.Round(ganancias / perdidas, 4), Decimal.MaxValue)

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

        Private Shared Sub Reportar(progreso As IProgress(Of String), mensaje As String)
            If progreso IsNot Nothing Then progreso.Report(mensaje)
            Console.WriteLine("[BacktestMotor] " & mensaje)
        End Sub

    End Class

End Namespace
