' ============================================================
' Tareas.vb
'
' Tareas en segundo plano que no se esperan (bucles de escucha,
' reconciliacion, refrescos de UI...). En vez de silenciar el
' aviso BC42358 y perder la excepcion si fallan, se registran en
' el log.
' ============================================================
Imports System.Threading.Tasks

Namespace WpfDerivClientVB

    Public Module Tareas

        ''' <summary>Deja correr la tarea sin esperarla; si falla, queda en el log con su descripcion.</summary>
        Public Sub EnSegundoPlano(tarea As Task, descripcion As String)
            If tarea Is Nothing Then Return
            tarea.ContinueWith(
                Sub(t) AppLogger.Error("[Tarea] Fallo en segundo plano: " & descripcion, t.Exception?.GetBaseException()),
                TaskContinuationOptions.OnlyOnFaulted)
        End Sub

    End Module

End Namespace
