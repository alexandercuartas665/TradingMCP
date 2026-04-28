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

                    Dim sqlIdx1 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_sesion ON public.backtest_operaciones(sesion_id);"
                    Dim sqlIdx2 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_estado ON public.backtest_operaciones(sesion_id, estado);"
                    Dim sqlIdx3 = "CREATE INDEX IF NOT EXISTS idx_bt_ops_cuenta ON public.backtest_operaciones(sesion_id, cuenta_nombre);"
                    Dim sqlIdx4 = "CREATE INDEX IF NOT EXISTS idx_bt_ses_tipo   ON public.backtest_sesiones(tipo_sesion);"

                    For Each ddl In {sqlSesiones, sqlOperaciones, sqlTipoSesion, sqlContractId,
                                     sqlIdx1, sqlIdx2, sqlIdx3, sqlIdx4}
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
        ''' Crea una nueva sesion de live testing en BD y retorna su ID.
        ''' </summary>
        Public Async Function CrearSesionLiveAsync(config As LiveTestConfig, nombreEstrategia As String) As Task(Of Integer)
            Dim sesionId As Integer = 0
            Try
                Using conn As New NpgsqlConnection(_connectionString)
                    Await conn.OpenAsync()
                    Dim sql =
                        "INSERT INTO public.backtest_sesiones " &
                        "  (estrategia, symbol, timeframe, desde_epoch, hasta_epoch, " &
                        "   duracion_velas, payout_pct, min_confianza, tipo_sesion) " &
                        "VALUES (@est, @sym, @tf, @de, @he, @dv, @pay, @mc, 'live') " &
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
                        "WHERE id = @id;"
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
                        "    win_rate = @wr, profit_loss_total = @plt " &
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

End Namespace
