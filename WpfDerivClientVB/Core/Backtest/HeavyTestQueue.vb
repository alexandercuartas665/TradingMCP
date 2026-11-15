' ============================================================
' HeavyTestQueue.vb
'
' Motor de cola para el Heavy Strategy Tester. Maneja:
'   - Generacion de combinaciones (producto cartesiano)
'   - Ejecucion paralela controlada (SemaphoreSlim)
'   - Reporte de progreso por job
'   - Cancelacion global
'
' Cada job ejecuta BacktestMotor.EjecutarAsync sobre la BD historica.
' ============================================================
Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Threading.Tasks
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB.Backtesting

    ''' <summary>Seleccion del usuario en el panel izquierdo (entrada para generar combinaciones).</summary>
    Public Class HeavySelection
        Public Property Activos As List(Of String) = New List(Of String)()
        Public Property Estrategias As List(Of String) = New List(Of String)()
        Public Property Timeframes As List(Of String) = New List(Of String)()
        Public Property Confianzas As List(Of Integer) = New List(Of Integer)()
        Public Property Payouts As List(Of Decimal) = New List(Of Decimal)()
        Public Property Duraciones As List(Of Integer) = New List(Of Integer)()
        Public Property Capitales As List(Of Decimal) = New List(Of Decimal)()
        Public Property DesdeEpoch As Long = 0L
        Public Property HastaEpoch As Long = 0L
        ''' <summary>True: cada job usa el payout real medido si existe (el rango de payouts se ignora).</summary>
        Public Property UsarPayoutReal As Boolean = False

        Public ReadOnly Property TotalCombinaciones As Integer
            Get
                Dim n As Long = 1L
                Dim listas As Integer() = {Activos.Count, Estrategias.Count, Timeframes.Count,
                                            Math.Max(1, Confianzas.Count), Math.Max(1, Payouts.Count),
                                            Math.Max(1, Duraciones.Count), Math.Max(1, Capitales.Count)}
                For Each c In listas
                    If c <= 0 Then Return 0
                    n *= c
                    If n > Integer.MaxValue Then Return Integer.MaxValue
                Next
                Return CInt(n)
            End Get
        End Property
    End Class

    Public Class HeavyTestQueue

        Public ReadOnly Property Jobs As New ObservableCollection(Of HeavyTestJob)()

        Private _cts As CancellationTokenSource = Nothing
        Private _enCurso As Boolean = False
        Public ReadOnly Property EnCurso As Boolean
            Get
                Return _enCurso
            End Get
        End Property

        ''' <summary>Disparado cuando un job termina (completado o error).</summary>
        Public Event JobFinalizado As Action(Of HeavyTestJob)
        ''' <summary>Disparado cuando todos los jobs de la cola terminaron.</summary>
        Public Event ColaFinalizada As Action

        ''' <summary>
        ''' Genera el producto cartesiano de la seleccion y agrega cada combinacion como
        ''' job pendiente. Si una lista de rangos esta vacia, se omite (se usa default 0).
        ''' Devuelve el numero de jobs agregados.
        ''' </summary>
        Public Function GenerarCombinaciones(seleccion As HeavySelection) As Integer
            If seleccion Is Nothing Then Return 0
            If seleccion.Activos.Count = 0 OrElse seleccion.Estrategias.Count = 0 OrElse
               seleccion.Timeframes.Count = 0 Then Return 0

            ' Asegurar al menos un valor en los rangos (defaults razonables si quedaron vacios)
            Dim confs = If(seleccion.Confianzas.Count > 0, seleccion.Confianzas, New List(Of Integer) From {55})
            Dim pays = If(seleccion.Payouts.Count > 0, seleccion.Payouts, New List(Of Decimal) From {85D})
            ' Con payout real cada job toma el medido: varios payouts darian jobs repetidos
            If seleccion.UsarPayoutReal Then pays = New List(Of Decimal) From {pays(0)}
            Dim durs = If(seleccion.Duraciones.Count > 0, seleccion.Duraciones, New List(Of Integer) From {5})
            Dim caps = If(seleccion.Capitales.Count > 0, seleccion.Capitales, New List(Of Decimal) From {100D})

            Dim agregados As Integer = 0
            For Each act In seleccion.Activos
                For Each est In seleccion.Estrategias
                    For Each tf In seleccion.Timeframes
                        For Each conf In confs
                            For Each pay In pays
                                For Each dur In durs
                                    For Each cap In caps
                                        Jobs.Add(New HeavyTestJob With {
                                            .Estrategia = est,
                                            .Symbol = act,
                                            .Timeframe = tf,
                                            .Confianza = conf,
                                            .Payout = pay,
                                            .Duracion = dur,
                                            .Capital = cap,
                                            .DesdeEpoch = seleccion.DesdeEpoch,
                                            .HastaEpoch = seleccion.HastaEpoch,
                                            .UsarPayoutReal = seleccion.UsarPayoutReal,
                                            .Estado = HeavyJobEstado.Pendiente
                                        })
                                        agregados += 1
                                    Next
                                Next
                            Next
                        Next
                    Next
                Next
            Next
            Return agregados
        End Function

        ''' <summary>Vacia la cola completamente. Solo si no hay ejecucion en curso.</summary>
        Public Sub Limpiar()
            If _enCurso Then Return
            Jobs.Clear()
        End Sub

        ''' <summary>
        ''' Elimina un job especifico de la cola. No permite eliminar jobs en
        ''' ejecucion (deben cancelarse primero via Cancelar()). Devuelve True si
        ''' se elimino, False si no se pudo (ejecutando o no existe).
        ''' </summary>
        Public Function EliminarJob(job As HeavyTestJob) As Boolean
            If job Is Nothing Then Return False
            If job.Estado = HeavyJobEstado.Ejecutando Then Return False
            Return Jobs.Remove(job)
        End Function

        ''' <summary>Cancela la ejecucion actual (si la hay) y marca pendientes como Cancelado.</summary>
        Public Sub Cancelar()
            If _cts IsNot Nothing Then
                Try
                    _cts.Cancel()
                Catch
                End Try
            End If
            ' Marcar jobs pendientes como cancelados (no podran iniciar)
            For Each j In Jobs.ToList()
                If j.Estado = HeavyJobEstado.Pendiente Then
                    j.Estado = HeavyJobEstado.Cancelado
                    j.MensajeError = "Cancelado por usuario"
                End If
            Next
        End Sub

        ''' <summary>
        ''' Ejecuta todos los jobs Pendiente en paralelo controlado por SemaphoreSlim.
        ''' Reporta avance via job.Progreso. Captura errores y los marca en MensajeError.
        ''' </summary>
        Public Async Function EjecutarTodasAsync(maxParalelo As Integer) As Task
            If _enCurso Then Return
            If maxParalelo < 1 Then maxParalelo = 1
            If maxParalelo > 8 Then maxParalelo = 8

            _cts = New CancellationTokenSource()
            _enCurso = True

            Try
                Dim sem As New SemaphoreSlim(maxParalelo, maxParalelo)
                Dim tareas As New List(Of Task)()

                For Each jobIter In Jobs.ToList()
                    Dim job = jobIter
                    If job.Estado <> HeavyJobEstado.Pendiente Then Continue For
                    If _cts.Token.IsCancellationRequested Then Exit For

                    Await sem.WaitAsync(_cts.Token)

                    Dim t = Task.Run(Async Function() As Task
                                         Try
                                             If _cts.Token.IsCancellationRequested Then
                                                 job.Estado = HeavyJobEstado.Cancelado
                                                 Return
                                             End If
                                             Await EjecutarUnJobAsync(job, _cts.Token)
                                         Finally
                                             sem.Release()
                                             RaiseEvent JobFinalizado(job)
                                         End Try
                                     End Function)
                    tareas.Add(t)
                Next

                Try
                    Await Task.WhenAll(tareas)
                Catch
                End Try
            Finally
                _enCurso = False
                _cts = Nothing
                RaiseEvent ColaFinalizada()
            End Try
        End Function

        ''' <summary>Ejecuta un job individual usando el BacktestMotor existente.</summary>
        Private Async Function EjecutarUnJobAsync(job As HeavyTestJob, ct As CancellationToken) As Task
            job.Estado = HeavyJobEstado.Ejecutando
            job.FechaInicio = DateTime.UtcNow
            job.Progreso = 5D

            Try
                Dim defEst = EstrategiasDinamicasRegistry.GetByNombre(job.Estrategia)
                If defEst Is Nothing Then
                    ' El registro solo se carga cuando alguien lo pide (p. ej. en modo agente sin ventanas)
                    EstrategiasDinamicasRegistry.Inicializar()
                    defEst = EstrategiasDinamicasRegistry.GetByNombre(job.Estrategia)
                End If
                If defEst Is Nothing Then
                    job.Estado = HeavyJobEstado.Error_
                    job.MensajeError = "Estrategia no encontrada en registry: " & job.Estrategia
                    job.FechaFin = DateTime.UtcNow
                    Return
                End If

                Dim cuenta As New BacktestCuenta With {
                    .NombreCuenta = "default",
                    .BalanceInicial = job.Capital,
                    .BalanceActual = job.Capital,
                    .MontoPorOperacion = 1D
                }
                ' Payout real medido en Deriv (si existe) en vez del supuesto
                If job.UsarPayoutReal Then
                    Dim real = PayoutsReales.Obtener(job.Symbol, job.Timeframe, job.Duracion)
                    If real.HasValue Then
                        job.Payout = real.Value
                        job.PayoutEsReal = True
                    End If
                End If
                Dim cfg As New BacktestConfig With {
                    .Symbol = job.Symbol,
                    .Timeframe = job.Timeframe,
                    .Platform = "Deriv",
                    .DesdeEpoch = job.DesdeEpoch,
                    .HastaEpoch = job.HastaEpoch,
                    .DuracionVelas = job.Duracion,
                    .PayoutPct = job.Payout,
                    .MinConfianza = job.Confianza,
                    .VentanaIndicadores = 100,
                    .GuardarContextoVelas = 0,
                    .UnaPosicionALaVez = True,   ' resultados independientes, igual que la validacion y el vivo
                    .Cuentas = New List(Of BacktestCuenta) From {cuenta}
                }

                ' Adaptador IProgress(Of String) → fases de arranque (carga de velas, etc.).
                ' Tope bajo (15%) para que el % real de velas tome el control sin retroceder.
                Dim faseActual As Integer = 0
                Dim progreso As New Progress(Of String)(Sub(msg)
                                                            faseActual += 1
                                                            job.Progreso = Math.Max(job.Progreso,
                                                                Math.Min(15D, 5D + faseActual * 5D))
                                                        End Sub)
                ' Adaptador IProgress(Of Metricas) → actualiza contadores en vivo del job.
                ' Math.Max evita que la barra retroceda si llega un reporte tardio.
                Dim progresoMetricas As New Progress(Of BacktestProgresoMetricas)(
                    Sub(m)
                        job.VelasAnalizadas = m.VelasAnalizadas
                        job.TotalVelas = m.TotalVelas
                        job.GanadasParcial = m.Ganadas
                        job.PerdidasParcial = m.Perdidas
                        If m.TotalVelas > 0 Then
                            Dim pct = Math.Min(95D, CDec(m.VelasAnalizadas) / CDec(m.TotalVelas) * 100D)
                            job.Progreso = Math.Max(job.Progreso, pct)
                        End If
                    End Sub)

                Dim motor As New BacktestMotor()
                Dim res = Await motor.EjecutarAsync(cfg, defEst, progreso, progresoMetricas, ct)

                If ct.IsCancellationRequested Then
                    job.Estado = HeavyJobEstado.Cancelado
                Else
                    job.Resultado = res
                    job.Progreso = 100D
                    job.Estado = HeavyJobEstado.Completado
                End If
            Catch ex As OperationCanceledException
                job.Estado = HeavyJobEstado.Cancelado
                job.MensajeError = "Cancelado por usuario"
            Catch ex As Exception
                job.Estado = HeavyJobEstado.Error_
                job.MensajeError = ex.Message
                AppLogger.Warn("[HeavyTestQueue] Job " & job.Id & " fallo: " & ex.Message)
            Finally
                job.FechaFin = DateTime.UtcNow
            End Try
        End Function

    End Class

End Namespace
