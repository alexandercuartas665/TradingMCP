Imports System

Namespace WpfDerivClientVB
    ''' <summary>
    ''' Modelo que representa a un cliente gestionado en el sistema.
    ''' </summary>
    Public Class ClientModel
        Public Property Id As Integer
        Public Property Name As String
        Public Property Email As String
        Public Property Active As Boolean = True
        Public Property CreatedAt As DateTime

        ' Credenciales para operar en Deriv
        Public Property DerivCredentials As ClientDerivCredentials
    End Class

    ''' <summary>
    ''' Credenciales separadas para las cuentas "Real" y "Virtual" de Deriv.
    ''' </summary>
    Public Class ClientDerivCredentials
        Public Property AppIdReal As String
        Public Property ApiTokenReal As String
        Public Property AppIdVirtual As String
        Public Property ApiTokenVirtual As String
    End Class

End Namespace
