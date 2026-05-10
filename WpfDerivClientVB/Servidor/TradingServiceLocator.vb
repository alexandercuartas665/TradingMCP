' ============================================================
' TradingServiceLocator.vb
'
' Localizador singleton que MainWindow registra al iniciar la app.
' Cualquier consumidor (LiveStrategiesController, CommandRouter, etc.)
' usa TradingServiceLocator.Current para invocar trades en Deriv.
'
' Diseno: Module estatico con backing field volatile-equivalente
' (la propiedad Current se lee/escribe dentro de un SyncLock).
' Registro idempotente: la primera implementacion gana hasta que se
' llame Reset() (util para tests).
' ============================================================
Imports System.Threading

Namespace WpfDerivClientVB.Servidor

    Public Module TradingServiceLocator

        Private ReadOnly _lock As New Object()
        Private _service As ITradingService = Nothing

        ''' <summary>Servicio actual; Nothing si no se ha registrado todavia.</summary>
        Public ReadOnly Property Current As ITradingService
            Get
                SyncLock _lock
                    Return _service
                End SyncLock
            End Get
        End Property

        ''' <summary>True si hay un servicio registrado y operativo.</summary>
        Public ReadOnly Property IsAvailable As Boolean
            Get
                SyncLock _lock
                    Return _service IsNot Nothing
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Registra el servicio activo. Sobrescribe el anterior si lo hay.
        ''' Llamar desde MainWindow al finalizar Loaded.
        ''' </summary>
        Public Sub Register(svc As ITradingService)
            If svc Is Nothing Then Throw New ArgumentNullException("svc")
            SyncLock _lock
                _service = svc
            End SyncLock
        End Sub

        ''' <summary>Limpia el registro (test/teardown).</summary>
        Public Sub Reset()
            SyncLock _lock
                _service = Nothing
            End SyncLock
        End Sub

    End Module

End Namespace
