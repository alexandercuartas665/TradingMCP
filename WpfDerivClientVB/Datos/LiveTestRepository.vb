' ============================================================
' LiveTestRepository.vb
'
' Repositorio PostgreSQL para sesiones de live testing.
' Reutiliza las mismas tablas de backtesting (backtest_sesiones
' y backtest_operaciones) pero agrega columnas especificas
' para live test mediante ALTER TABLE IF NOT EXISTS.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Npgsql
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Configuracion de una sesion de live testing.
    ''' </summary>
    Public Class LiveTestConfig
        Public Property Symbol               As String = ""
        Public Property Timeframe            As String = ""
        ''' <summary>Cuantas velas esperar para resolver cada trade.</summary>
        Public Property DuracionVelas        As Integer = 5
        ''' <summary>Payout porcentaje (ej: 85 para ganar 85% del monto).</summary>
        Public Property PayoutPct            As Decimal = 85D
        ''' <summary>Confianza minima para abrir un trade (0-100).</summary>
        Public Property MinConfianza         As Integer = 55
        ''' <summary>Monto a invertir por operacion.</summary>
        Public Property MontoOperacion       As Decimal = 1D
        ''' <summary>Cuantas velas de historia necesita la estrategia.</summary>
        Public Property VentanaIndicadores   As Integer = 100
        ''' <summary>Cuantas velas previas guardar en contexto de perdida.</summary>
        Public Property GuardarContextoVelas As Integer = 5
        ''' <summary>Limite en horas de operacion del bot (0 = sin limite). Auto-pausa al alcanzarlo.</summary>
        Public Property HorasOperacion       As Integer = 0
        ''' <summary>Limite de ganancia acumulada (0 = sin limite). Auto-pausa al alcanzarlo.</summary>
        Public Property LimiteGanancia       As Decimal = 0D
        ''' <summary>Limite de perdida acumulada (0 = sin limite, valor positivo). Auto-pausa al alcanzarlo.</summary>
        Public Property LimitePerdida        As Decimal = 0D
        ''' <summary>
        ''' True = no abre una operacion mientras haya otra abierta (igual que el backtest
        ''' validado con UnaPosicionALaVez) y solo opera velas recien cerradas (no velas
        ''' atrasadas tras una reconexion). Lo activa LiveStrategiesController.
        ''' </summary>
        Public Property ModoForward          As Boolean = False
    End Class

    ''' <summary>
    ''' Repositorio para guardar sesiones y operaciones de live testing.
    ''' Patron identico a BacktestRepository.
    ''' </summary>
    Public Class LiveTestRepository

        Private ReadOnly _connectionString As String

        Public Sub New()
            Dim config = ConfigManager.LoadDbConfig()
            _connectionString = config.GetConnectionString()
        End Sub

        ' ================================================================
        '  DDL — agrega columnas a las tablas de backtesting
        ' ================================================================

        ''' <summary>
        ''' Asegura que las tablas de backtesting existan y agrega las columnas
        ''' tipo_sesion y contract_id si no existen todavia.
        ''' Seguro de ejecutar multiples veces (idempotente).
        ''' </summary>
        Public Async Function EnsureTablesExistAsync() As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()

                    ' Asegurar que las tablas base existen (crearlas si es primera vez)
                    Dim sqlSesiones =
                        "CREATE TABLE IF NOT EXISTS public.backtest_sesiones (" &
                        "    id                SERIAL PRIMARY KEY," &
                        "    estrategia        VARCHAR(50)     NOT NULL," &
                        "    symbol            VARCHAR(50)     NOT NULL," &
                        "    timeframe         VARCHAR(10)     NOT NULL," &
                        "    desde_epoch       BIGINT          NOT NULL," &
                        "    hasta_epoch       BIGINT          NOT NULL," &
                        "    duracion_velas    INTEGER         NOT NULL," &
                        "    payout_pct        DECIMAL(6,2)    NOT NULL," &
                        "    min_confianza     INTEGER         NOT NULL," &
                        "    total_senales     INTEGER         NOT NULL DEFAULT 0," &
                        "    total_operaciones INTEGER         NOT NULL DEFAULT 0," &
                        "    win_rate          DECIMAL(6,4)    NOT NULL DEFAULT 0," &
                        "    profit_loss_total DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    max_drawdown      DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    profit_factor     DECIMAL(10,4)   NOT NULL DEFAULT 0," &
                        "    created_at        TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP," &
                        "    notas             TEXT" &
                        ");"

                    Dim sqlOperaciones =
                        "CREATE TABLE IF NOT EXISTS public.backtest_operaciones (" &
                        "    id               SERIAL PRIMARY KEY," &
                        "    sesion_id        INTEGER         NOT NULL REFERENCES public.backtest_sesiones(id) ON DELETE CASCADE," &
                        "    cuenta_nombre    VARCHAR(100)    NOT NULL," &
                        "    tipo             VARCHAR(4)      NOT NULL," &
                        "    epoch_entrada    BIGINT          NOT NULL," &
                        "    precio_entrada   DECIMAL(18,6)   NOT NULL," &
                        "    epoch_salida     BIGINT          NOT NULL DEFAULT 0," &
                        "    precio_salida    DECIMAL(18,6)   NOT NULL DEFAULT 0," &
                        "    monto            DECIMAL(14,4)   NOT NULL," &
                        "    payout_pct       DECIMAL(6,2)    NOT NULL," &
                        "    estado           VARCHAR(10)     NOT NULL," &
                        "    profit_loss      DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    balance_post     DECIMAL(14,4)   NOT NULL DEFAULT 0," &
                        "    confianza        INTEGER         NOT NULL," &
                        "    motivo_senal     VARCHAR(500)," &
                        "    datos_indicadores TEXT," &
                        "    contexto_velas   TEXT," &
                        "    created_at       TIMESTAMP       NOT NULL DEFAULT CURRENT_TIMESTAMP" &
                        ");"

                    ' Agregar columnas live-test si no existen
                    Dim sqlTipoSesion =
                        "ALTER TABLE public.backtest_sesiones " &
                        "ADD COLUMN IF NOT EXISTS tipo_sesion VARCHAR(20) NOT NULL DEFAULT 'backtest';"

                    Dim sqlContractId =
                        "ALTER TABLE public.backtest_operaciones " &
                        "ADD COLUMN IF NOT EXISTS contract_id VARCHAR(100);"

                    ' estado era VARCHAR(10): no cabian "NoEjecutada" ni "BloqueadaRiesgo" y esas
                    ' operaciones se quedaban en "Pendiente". Ampliar solo si hace falta.
                    Dim sqlEstado20 =
                        "DO $$ BEGIN " &
                        "IF (SELECT character_maximum_length FROM information_schema.columns " &
                        "    WHERE table_schema = 'public' AND table_name = 'backtest_operaciones' AND column_name = 'estado') < 20 THEN " &
                        "  ALTER TABLE public.backtest_operaciones ALTER COLUMN estado TYPE VARCHAR(20); " &
                        "END IF; END $$;"

                    ' Flag de "activa" para detectar sesiones live que crashearon sin cerrarse.
                    Dim sqlActiva =
                        "ALTER TABLE public.backtest_sesiones " &
                        "ADD COLUMN IF NOT EXISTS activa BOOLEAN NOT NULL DEFAULT false;"

                    Dim sqlIdx1 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_sesion ON public.backtest_operaciones(sesion_id);"
                    Dim sqlIdx2 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_estado ON public.backtest_operaciones(sesion_id, estado);"
                    Dim sqlIdx3 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_cuenta ON public.backtest_operaciones(sesion_id, cuenta_nombre);"
                    Dim sqlIdx4 = "CREATE INDEX IF NOT EXISTS idx_bt_ses_tipo   ON public.backtest_sesiones(tipo_sesion);"
                    Dim sqlIdx5 = "CREATE INDEX IF NOT EXISTS idx_bt_ses_activa ON public.backtest_sesiones(activa) WHERE activa = true;"

                    For Each ddl In {sqlSesiones, sqlOperaciones, sqlTipoSesion, sqlContractId, sqlActiva, sqlEstado20,
                                     sqlIdx1, sqlIdx2, sqlIdx3, sqlIdx4, sqlIdx5}
                        Using cmd As New NpgsqlCommand(ddl, conn)
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next
                End Using
            Catch ex As Exception
                Throw New Exception("Error al inicializar tablas de live testing: " & ex.Message)
            End Try
        End Function

        ' ================================================================
        '  ESCRITURA
        ' ================================================================

        ''' <summary>
        ''' Crea una nueva sesion en BD y retorna su ID.
        '''   - tipoSesion="live" + activa=true → motor real, ejecuta trades en Deriv
        '''   - tipoSesion="backtest" + activa=false → simulacion sobre datos historicos (Replay BD)
        ''' </summary>
        Public Async Function CrearSesionLiveAsync(config As LiveTestConfig, nombreEstrategia As String,
                                                    Optional tipoSesion As String = "live") As Task(Of Integer)
            Dim sesionId As Integer = 0
            Dim activa As Boolean = (tipoSesion = "live")
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "INSERT INTO public.backtest_sesiones " &
                        "  (estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "   duracion_velas, payout_pct, min_confianza, tipo_sesion, activa) " &
                        "VALUES (@est, @sym, @tf, @de, @he, @dv, @pay, @mc, @ts, @act) " &
                        "RETURNING id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("est", nombreEstrategia)
                        cmd.Parameters.AddWithValue("sym", config.Symbol)
                        cmd.Parameters.AddWithValue("tf",  config.Timeframe)
                        cmd.Parameters.AddWithValue("de",  DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                        cmd.Parameters.AddWithValue("he",  0L)
                        cmd.Parameters.AddWithValue("dv",  config.DuracionVelas)
                        cmd.Parameters.AddWithValue("pay", config.PayoutPct)
                        cmd.Parameters.AddWithValue("mc",  config.MinConfianza)
                        cmd.Parameters.AddWithValue("ts",  tipoSesion)
                        cmd.Parameters.AddWithValue("act", activa)
                        sesionId = CInt(Await cmd.ExecuteScalarAsync())
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.CrearSesionLiveAsync] Error: " & ex.Message)
                Throw
            End Try
            Return sesionId
        End Function

        ''' <summary>
        ''' Guarda una nueva operacion en estado "Pendiente" o inicial.
        ''' Retorna el ID de la operacion creada.
        ''' </summary>
        Public Async Function GuardarOperacionAsync(op As BacktestOperacion, Optional contractId As String = "") As Task(Of Integer)
            Dim opId As Integer = 0
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "INSERT INTO public.backtest_operaciones " &
                        "  (sesion_id, cuenta_nombre, tipo, epoch_entrada, precio_entrada, " &
                        "   epoch_salida, precio_salida, monto, payout_pct, estado, " &
                        "   profit_loss, balance_post, confianza, motivo_senal, " &
                        "   datos_indicadores, contexto_velas, contract_id) " &
                        "VALUES (@sid, @cn, @tipo, @ee, @pe, @es, @ps, @monto, @pay, @est, " &
                        "        @pl, @bp, @conf, @mot, @di, @cv, @cid) " &
                        "RETURNING id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("sid",  op.SesionId)
                        cmd.Parameters.AddWithValue("cn",   op.CuentaNombre)
                        cmd.Parameters.AddWithValue("tipo", op.Tipo)
                        cmd.Parameters.AddWithValue("ee",   op.EpochEntrada)
                        cmd.Parameters.AddWithValue("pe",   op.PrecioEntrada)
                        cmd.Parameters.AddWithValue("es",   op.EpochSalida)
                        cmd.Parameters.AddWithValue("ps",   op.PrecioSalida)
                        cmd.Parameters.AddWithValue("monto", op.Monto)
                        cmd.Parameters.AddWithValue("pay",  op.PayoutPct)
                        cmd.Parameters.AddWithValue("est",  op.Estado)
                        cmd.Parameters.AddWithValue("pl",   op.ProfitLoss)
                        cmd.Parameters.AddWithValue("bp",   op.BalancePost)
                        cmd.Parameters.AddWithValue("conf", op.Confianza)
                        cmd.Parameters.AddWithValue("mot",  If(op.MotivoSenal <> "", CType(op.MotivoSenal, Object), DBNull.Value))
                        cmd.Parameters.AddWithValue("di",   If(op.DatosIndicadores <> "", CType(op.DatosIndicadores, Object), DBNull.Value))
                        cmd.Parameters.AddWithValue("cv",   If(op.ContextoVelas <> "", CType(op.ContextoVelas, Object), DBNull.Value))
                        cmd.Parameters.AddWithValue("cid",  If(contractId <> "", CType(contractId, Object), DBNull.Value))
                        opId = CInt(Await cmd.ExecuteScalarAsync())
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GuardarOperacionAsync] Error: " & ex.Message)
                Throw
            End Try
            Return opId
        End Function

        ''' <summary>
        ''' Actualiza una operacion pendiente con su resultado final.
        ''' </summary>
        Public Async Function ActualizarOperacionAsync(opId As Integer, precioSalida As Decimal,
                                                        epochSalida As Long, estado As String,
                                                        pnl As Decimal, balancePost As Decimal,
                                                        Optional contextoVelas As String = "") As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "UPDATE public.backtest_operaciones " &
                        "SET precio_salida = @ps, epoch_salida = @es, estado = @est, " &
                        "    profit_loss = @pl, balance_post = @bp, " &
                        "    contexto_velas = CASE WHEN @cv <> '' THEN @cv ELSE contexto_velas END " &
                        "WHERE id = @id AND estado = 'Pendiente';"   ' nunca pisar un resultado real ya registrado
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("ps",  precioSalida)
                        cmd.Parameters.AddWithValue("es",  epochSalida)
                        cmd.Parameters.AddWithValue("est", estado)
                        cmd.Parameters.AddWithValue("pl",  pnl)
                        cmd.Parameters.AddWithValue("bp",  balancePost)
                        cmd.Parameters.AddWithValue("cv",  If(contextoVelas <> "", contextoVelas, ""))
                        cmd.Parameters.AddWithValue("id",  opId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.ActualizarOperacionAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Cierra una sesion live actualizando metricas finales.
        ''' </summary>
        Public Async Function CerrarSesionAsync(sesionId As Integer, totalOps As Integer,
                                                 totalSenales As Integer, winRate As Decimal,
                                                 pnlTotal As Decimal) As Task
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "UPDATE public.backtest_sesiones " &
                        "SET hasta_epoch = @he, total_operaciones = @top, total_senales = @ts, " &
                        "    win_rate = @wr, profit_loss_total = @plt, activa = false " &
                        "WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("he",  DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                        cmd.Parameters.AddWithValue("top", totalOps)
                        cmd.Parameters.AddWithValue("ts",  totalSenales)
                        cmd.Parameters.AddWithValue("wr",  winRate)
                        cmd.Parameters.AddWithValue("plt", pnlTotal)
                        cmd.Parameters.AddWithValue("id",  sesionId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.CerrarSesionAsync] Error: " & ex.Message)
            End Try
        End Function

        ' ================================================================
        '  LECTURA
        ' ================================================================

        ''' <summary>
        ''' Retorna las sesiones live mas recientes.
        ''' </summary>
        Public Async Function GetSesionesLiveAsync(Optional limite As Integer = 20) As Task(Of List(Of BacktestSesion))
            Dim lista As New List(Of BacktestSesion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "       duracion_velas, payout_pct, min_confianza, total_senales, " &
                        "       total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                        "       profit_factor, created_at, notas " &
                        "FROM public.backtest_sesiones " &
                        "WHERE tipo_sesion = 'live' " &
                        "ORDER BY created_at DESC LIMIT @lim;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapSesion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetSesionesLiveAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Retorna las operaciones de una sesion, ordenadas por epoch de entrada.
        ''' </summary>
        Public Async Function GetOperacionesPorSesionAsync(sesionId As Integer) As Task(Of List(Of BacktestOperacion))
            Dim lista As New List(Of BacktestOperacion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, sesion_id, cuenta_nombre, tipo, epoch_entrada, precio_entrada, " &
                        "       epoch_salida, precio_salida, monto, payout_pct, estado, profit_loss, " &
                        "       balance_post, confianza, motivo_senal, datos_indicadores, contexto_velas " &
                        "FROM public.backtest_operaciones " &
                        "WHERE sesion_id = @sid ORDER BY epoch_entrada ASC;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("sid", sesionId)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapOperacion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetOperacionesPorSesionAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Retorna las sesiones live marcadas como activas (huerfanas si el motor en RAM
        ''' ya no existe). Usado para auto-reanudar tras un crash.
        ''' </summary>
        Public Async Function GetSesionesLiveActivasAsync() As Task(Of List(Of BacktestSesion))
            Dim lista As New List(Of BacktestSesion)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT id, estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "       duracion_velas, payout_pct, min_confianza, total_senales, " &
                        "       total_operaciones, win_rate, profit_loss_total, max_drawdown, " &
                        "       profit_factor, created_at, notas " &
                        "FROM public.backtest_sesiones " &
                        "WHERE tipo_sesion = 'live' AND activa = true " &
                        "ORDER BY created_at DESC;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(MapSesion(reader))
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetSesionesLiveActivasAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Retorna las ultimas N operaciones live (cualquier estado: Pendiente/Abierto/Ganado/Perdido/Expirado).
        ''' Usado para llenar el grid de Trading Live tras restart con el historial reciente.
        ''' </summary>
        Public Async Function GetUltimasOperacionesLiveAsync(Optional limite As Integer = 100) As Task(Of List(Of OperacionPendienteInfo))
            Dim lista As New List(Of OperacionPendienteInfo)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    ' SOLO ops que realmente se ejecutaron en Deriv (contract_id no nulo).
                    ' Las ops sin contract_id son fantasmas de simulacion o de proposals fallidos
                    ' y no deben aparecer en el grid de Trading Live (real).
                    Dim sql =
                        "SELECT o.id, o.sesion_id, o.cuenta_nombre, o.tipo, o.epoch_entrada, " &
                        "       o.precio_entrada, o.epoch_salida, o.precio_salida, " &
                        "       o.monto, o.payout_pct, o.estado, o.profit_loss, " &
                        "       o.contract_id, s.estrategia, s.symbol, s.timeframe " &
                        "FROM public.backtest_operaciones o " &
                        "JOIN public.backtest_sesiones s ON s.id = o.sesion_id " &
                        "WHERE s.tipo_sesion = 'live' " &
                        "  AND o.estado <> 'Oculto' " &
                        "  AND o.contract_id IS NOT NULL " &
                        "  AND o.contract_id <> '' " &
                        "ORDER BY o.epoch_entrada DESC " &
                        "LIMIT @lim;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("lim", limite)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(New OperacionPendienteInfo With {
                                    .OperacionId   = CInt(reader("id")),
                                    .SesionId      = CInt(reader("sesion_id")),
                                    .CuentaNombre  = reader("cuenta_nombre").ToString(),
                                    .Tipo          = reader("tipo").ToString(),
                                    .EpochEntrada  = CLng(reader("epoch_entrada")),
                                    .PrecioEntrada = CDec(reader("precio_entrada")),
                                    .EpochSalida   = CLng(reader("epoch_salida")),
                                    .PrecioSalida  = CDec(reader("precio_salida")),
                                    .Monto         = CDec(reader("monto")),
                                    .PayoutPct     = CDec(reader("payout_pct")),
                                    .Estado        = reader("estado").ToString(),
                                    .ProfitLoss    = CDec(reader("profit_loss")),
                                    .ContractId    = If(IsDBNull(reader("contract_id")), "", reader("contract_id").ToString()),
                                    .Estrategia    = reader("estrategia").ToString(),
                                    .Symbol        = reader("symbol").ToString(),
                                    .Timeframe     = reader("timeframe").ToString()
                                })
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetUltimasOperacionesLiveAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Marca una operacion como "Oculto" para que no vuelva a aparecer en el grid
        ''' tras reinicios. Usado por el boton 🗑 cuando el usuario quiere quitar una op.
        ''' </summary>
        Public Async Function OcultarOperacionPorIdAsync(operacionId As Integer) As Task
            If operacionId <= 0 Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "UPDATE public.backtest_operaciones SET estado = 'Oculto' WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("id", operacionId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.OcultarOperacionPorIdAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Devuelve metricas acumuladas de una sesion (ganadas, perdidas, pnl_total) y
        ''' la lista de ops Pendiente/Abierto con contract_id (las que el motor debe
        ''' restaurar como "externas" al reanudar tras un crash).
        ''' </summary>
        Public Async Function GetMetricasYAbiertasSesionAsync(sesionId As Integer) _
                As Task(Of (Ganadas As Integer, Perdidas As Integer, PnlTotal As Decimal,
                            Abiertas As List(Of OperacionPendienteInfo)))
            Dim ganadas As Integer = 0, perdidas As Integer = 0
            Dim pnl As Decimal = 0D
            Dim abiertas As New List(Of OperacionPendienteInfo)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    ' Metricas
                    Dim sqlMetricas =
                        "SELECT " &
                        "  COUNT(*) FILTER (WHERE estado = 'Ganado') AS ganadas, " &
                        "  COUNT(*) FILTER (WHERE estado = 'Perdido') AS perdidas, " &
                        "  COALESCE(SUM(profit_loss) FILTER (WHERE estado IN ('Ganado','Perdido')), 0) AS pnl " &
                        "FROM public.backtest_operaciones " &
                        "WHERE sesion_id = @sid " &
                        "  AND contract_id IS NOT NULL AND contract_id <> '';"
                    Using cmd As New NpgsqlCommand(sqlMetricas, conn)
                        cmd.Parameters.AddWithValue("sid", sesionId)
                        Using r = Await cmd.ExecuteReaderAsync()
                            If Await r.ReadAsync() Then
                                ganadas = CInt(r("ganadas"))
                                perdidas = CInt(r("perdidas"))
                                pnl = CDec(r("pnl"))
                            End If
                        End Using
                    End Using
                    ' Ops abiertas
                    Dim sqlAbiertas =
                        "SELECT id, tipo, epoch_entrada, precio_entrada, monto, contract_id " &
                        "FROM public.backtest_operaciones " &
                        "WHERE sesion_id = @sid " &
                        "  AND estado IN ('Pendiente', 'Abierto') " &
                        "  AND contract_id IS NOT NULL AND contract_id <> '';"
                    Using cmd2 As New NpgsqlCommand(sqlAbiertas, conn)
                        cmd2.Parameters.AddWithValue("sid", sesionId)
                        Using r = Await cmd2.ExecuteReaderAsync()
                            Do While Await r.ReadAsync()
                                abiertas.Add(New OperacionPendienteInfo With {
                                    .OperacionId = CInt(r("id")),
                                    .Tipo = r("tipo").ToString(),
                                    .EpochEntrada = CLng(r("epoch_entrada")),
                                    .PrecioEntrada = CDec(r("precio_entrada")),
                                    .Monto = CDec(r("monto")),
                                    .ContractId = r("contract_id").ToString()
                                })
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetMetricasYAbiertasSesionAsync] " & ex.Message)
            End Try
            Return (ganadas, perdidas, pnl, abiertas)
        End Function

        ''' <summary>
        ''' Calcula el PnL acumulado de operaciones live cerradas (Ganado/Perdido) en
        ''' los periodos hoy, esta semana (lunes-domingo) y este mes (UTC).
        ''' Solo considera ops con contract_id (las reales en Deriv).
        ''' </summary>
        Public Async Function GetPnlPeriodosAsync() As Task(Of (Hoy As Decimal, Semana As Decimal, Mes As Decimal))
            Dim hoy As Decimal = 0D, semana As Decimal = 0D, mes As Decimal = 0D
            Try
                ' Limites de tiempo en UTC epoch
                Dim ahora = DateTime.UtcNow
                Dim inicioHoy = New DateTime(ahora.Year, ahora.Month, ahora.Day, 0, 0, 0, DateTimeKind.Utc)
                ' Lunes como inicio de semana
                Dim diff = CInt(ahora.DayOfWeek) - CInt(DayOfWeek.Monday)
                If diff < 0 Then diff += 7
                Dim inicioSemana = inicioHoy.AddDays(-diff)
                Dim inicioMes = New DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Utc)

                Dim epochHoy = New DateTimeOffset(inicioHoy).ToUnixTimeSeconds()
                Dim epochSemana = New DateTimeOffset(inicioSemana).ToUnixTimeSeconds()
                Dim epochMes = New DateTimeOffset(inicioMes).ToUnixTimeSeconds()

                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT " &
                        "  COALESCE(SUM(o.profit_loss) FILTER (WHERE o.epoch_entrada >= @hoy), 0) AS pnl_hoy, " &
                        "  COALESCE(SUM(o.profit_loss) FILTER (WHERE o.epoch_entrada >= @sem), 0) AS pnl_semana, " &
                        "  COALESCE(SUM(o.profit_loss) FILTER (WHERE o.epoch_entrada >= @mes), 0) AS pnl_mes " &
                        "FROM public.backtest_operaciones o " &
                        "JOIN public.backtest_sesiones s ON s.id = o.sesion_id " &
                        "WHERE s.tipo_sesion = 'live' " &
                        "  AND o.contract_id IS NOT NULL AND o.contract_id <> '' " &
                        "  AND o.estado IN ('Ganado','Perdido');"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("hoy", epochHoy)
                        cmd.Parameters.AddWithValue("sem", epochSemana)
                        cmd.Parameters.AddWithValue("mes", epochMes)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            If Await reader.ReadAsync() Then
                                hoy    = CDec(reader("pnl_hoy"))
                                semana = CDec(reader("pnl_semana"))
                                mes    = CDec(reader("pnl_mes"))
                            End If
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetPnlPeriodosAsync] Error: " & ex.Message)
            End Try
            Return (hoy, semana, mes)
        End Function

        ''' <summary>
        ''' Marca como "Oculto" la operacion con el contract_id dado. Util cuando no
        ''' tenemos el id interno de BD pero si el contract_id de Deriv.
        ''' </summary>
        Public Async Function OcultarOperacionPorContractIdAsync(contractId As String) As Task
            If String.IsNullOrEmpty(contractId) Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "UPDATE public.backtest_operaciones SET estado = 'Oculto' WHERE contract_id = @cid;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("cid", contractId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.OcultarOperacionPorContractIdAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Retorna todas las operaciones live "Pendiente" o "Abierto" con contract_id.
        ''' Usado al re-conectar a Trading WS para reconciliar trades dejados huerfanos
        ''' por un crash o cierre de la app.
        ''' Devuelve: tuplas (operacion, sesionId, estrategia, symbol).
        ''' </summary>
        Public Async Function GetOperacionesPendientesAsync() As Task(Of List(Of OperacionPendienteInfo))
            Dim lista As New List(Of OperacionPendienteInfo)()
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "SELECT o.id, o.sesion_id, o.cuenta_nombre, o.tipo, o.epoch_entrada, " &
                        "       o.precio_entrada, o.monto, o.payout_pct, o.estado, " &
                        "       o.contract_id, s.estrategia, s.symbol, s.timeframe " &
                        "FROM public.backtest_operaciones o " &
                        "JOIN public.backtest_sesiones s ON s.id = o.sesion_id " &
                        "WHERE s.tipo_sesion = 'live' " &
                        "  AND o.estado IN ('Pendiente', 'Abierto') " &
                        "  AND o.contract_id IS NOT NULL AND o.contract_id <> '' " &
                        "ORDER BY o.epoch_entrada DESC;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        Using reader = Await cmd.ExecuteReaderAsync()
                            Do While Await reader.ReadAsync()
                                lista.Add(New OperacionPendienteInfo With {
                                    .OperacionId   = CInt(reader("id")),
                                    .SesionId      = CInt(reader("sesion_id")),
                                    .CuentaNombre  = reader("cuenta_nombre").ToString(),
                                    .Tipo          = reader("tipo").ToString(),
                                    .EpochEntrada  = CLng(reader("epoch_entrada")),
                                    .PrecioEntrada = CDec(reader("precio_entrada")),
                                    .Monto         = CDec(reader("monto")),
                                    .PayoutPct     = CDec(reader("payout_pct")),
                                    .Estado        = reader("estado").ToString(),
                                    .ContractId    = reader("contract_id").ToString(),
                                    .Estrategia    = reader("estrategia").ToString(),
                                    .Symbol        = reader("symbol").ToString(),
                                    .Timeframe     = reader("timeframe").ToString()
                                })
                            Loop
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.GetOperacionesPendientesAsync] Error: " & ex.Message)
            End Try
            Return lista
        End Function

        ''' <summary>
        ''' Actualiza el contract_id de una operacion ya guardada. Se llama desde
        ''' AbrirTradeAsync despues de recibir el contract_id de Deriv.
        ''' Sin esto, la reconciliacion no encuentra la op (filtra contract_id NULL/'').
        ''' </summary>
        Public Async Function ActualizarContractIdAsync(operacionId As Integer, contractId As String) As Task
            If operacionId <= 0 OrElse String.IsNullOrEmpty(contractId) Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "UPDATE public.backtest_operaciones SET contract_id = @cid, estado = 'Abierto' " &
                              "WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("id", operacionId)
                        cmd.Parameters.AddWithValue("cid", contractId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.ActualizarContractIdAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Marca una operacion live como expirada/perdida cuando Deriv reporta ContractNotFound.
        ''' Esto sucede cuando el contrato cerro hace tiempo y ya no esta en open positions.
        ''' </summary>
        ''' <summary>
        ''' Marca una operacion live que NO llego a ejecutarse en Deriv (compra fallida,
        ''' rechazada o bloqueada por el gestor de riesgo). Antes quedaba 'Pendiente' y
        ''' el motor la "resolvia" con la simulacion de velas: ganancia/perdida fantasma.
        ''' </summary>
        Public Async Function MarcarOperacionNoEjecutadaAsync(operacionId As Integer, estado As String) As Task
            If operacionId <= 0 Then Return
            Using conn As New NpgsqlConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd As New NpgsqlCommand(
                    "UPDATE public.backtest_operaciones SET estado = @est, profit_loss = 0 " &
                    "WHERE id = @id AND estado = 'Pendiente';", conn)
                    cmd.Parameters.AddWithValue("est", estado)
                    cmd.Parameters.AddWithValue("id", operacionId)
                    Await cmd.ExecuteNonQueryAsync()
                End Using
            End Using
        End Function

        Public Async Function MarcarOperacionExpiradaPorContractIdAsync(contractId As String) As Task
            If String.IsNullOrEmpty(contractId) Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql = "UPDATE public.backtest_operaciones SET estado = 'Expirado' " &
                              "WHERE contract_id = @cid AND estado IN ('Pendiente', 'Abierto');"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("cid", contractId)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.MarcarOperacionExpiradaPorContractIdAsync] Error: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' Marca una operacion como cerrada con su resultado final (al recibir update de Deriv).
        ''' </summary>
        Public Async Function CerrarOperacionConResultadoAsync(operacionId As Integer,
                                                                estado As String,
                                                                profitLoss As Decimal,
                                                                precioSalida As Decimal,
                                                                epochSalida As Long) As Task
            If operacionId <= 0 Then Return
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "UPDATE public.backtest_operaciones SET " &
                        "  estado = @estado, profit_loss = @pnl, " &
                        "  precio_salida = @precio_salida, epoch_salida = @epoch_salida " &
                        "WHERE id = @id;"
                    Using cmd As New NpgsqlCommand(sql, conn)
                        cmd.Parameters.AddWithValue("id", operacionId)
                        cmd.Parameters.AddWithValue("estado", estado)
                        cmd.Parameters.AddWithValue("pnl", profitLoss)
                        cmd.Parameters.AddWithValue("precio_salida", precioSalida)
                        cmd.Parameters.AddWithValue("epoch_salida", epochSalida)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                End Using
            Catch ex As Exception
                AppLogger.Warn("[LiveTestRepository.CerrarOperacionConResultadoAsync] Error: " & ex.Message)
            End Try
        End Function

        ' ── Helpers de mapeo ────────────────────────────────────────────
        Private Shared Function MapSesion(reader As NpgsqlDataReader) As BacktestSesion
            Return New BacktestSesion With {
                .Id               = CInt(reader("id")),
                .Estrategia       = reader("estrategia").ToString(),
                .Symbol           = reader("symbol").ToString(),
                .Timeframe        = reader("timeframe").ToString(),
                .DesdeEpoch       = CLng(reader("desde_epoch")),
                .HastaEpoch       = CLng(reader("hasta_epoch")),
                .DuracionVelas    = CInt(reader("duracion_velas")),
                .PayoutPct        = CDec(reader("payout_pct")),
                .MinConfianza     = CInt(reader("min_confianza")),
                .TotalSenales     = CInt(reader("total_senales")),
                .TotalOperaciones = CInt(reader("total_operaciones")),
                .WinRate          = CDec(reader("win_rate")),
                .ProfitLossTotal  = CDec(reader("profit_loss_total")),
                .MaxDrawdown      = CDec(reader("max_drawdown")),
                .ProfitFactor     = CDec(reader("profit_factor")),
                .CreatedAt        = CDate(reader("created_at")),
                .Notas            = If(IsDBNull(reader("notas")), "", reader("notas").ToString())
            }
        End Function

        Private Shared Function MapOperacion(reader As NpgsqlDataReader) As BacktestOperacion
            Return New BacktestOperacion With {
                .SesionId         = CInt(reader("sesion_id")),
                .CuentaNombre     = reader("cuenta_nombre").ToString(),
                .Tipo             = reader("tipo").ToString(),
                .EpochEntrada     = CLng(reader("epoch_entrada")),
                .PrecioEntrada    = CDec(reader("precio_entrada")),
                .EpochSalida      = CLng(reader("epoch_salida")),
                .PrecioSalida     = CDec(reader("precio_salida")),
                .Monto            = CDec(reader("monto")),
                .PayoutPct        = CDec(reader("payout_pct")),
                .Estado           = reader("estado").ToString(),
                .ProfitLoss       = CDec(reader("profit_loss")),
                .BalancePost      = CDec(reader("balance_post")),
                .Confianza        = CInt(reader("confianza")),
                .MotivoSenal      = If(IsDBNull(reader("motivo_senal")),      "", reader("motivo_senal").ToString()),
                .DatosIndicadores = If(IsDBNull(reader("datos_indicadores")), "", reader("datos_indicadores").ToString()),
                .ContextoVelas    = If(IsDBNull(reader("contexto_velas")),    "", reader("contexto_velas").ToString())
            }
        End Function

    End Class

    ''' <summary>
    ''' DTO con los datos minimos de una operacion pendiente para reconciliar
    ''' tras un reinicio. Incluye datos de la sesion (estrategia/symbol/tf).
    ''' </summary>
    Public Class OperacionPendienteInfo
        Public Property OperacionId   As Integer
        Public Property SesionId      As Integer
        Public Property CuentaNombre  As String
        Public Property Tipo          As String
        Public Property EpochEntrada  As Long
        Public Property PrecioEntrada As Decimal
        Public Property EpochSalida   As Long
        Public Property PrecioSalida  As Decimal
        Public Property Monto         As Decimal
        Public Property PayoutPct     As Decimal
        Public Property Estado        As String
        Public Property ProfitLoss    As Decimal
        Public Property ContractId    As String
        Public Property Estrategia    As String
        Public Property Symbol        As String
        Public Property Timeframe     As String
    End Class

End Namespace
