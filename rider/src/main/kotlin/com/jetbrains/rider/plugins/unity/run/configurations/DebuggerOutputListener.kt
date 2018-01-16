package com.jetbrains.rider.plugins.unity.run.configurations

import com.intellij.execution.ui.ConsoleView
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.notification.NotificationType
import com.intellij.openapi.project.Project
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.impl.XDebuggerManagerImpl
import com.jetbrains.rider.debugger.DotNetDebugProcess
import com.jetbrains.rider.model.debuggerWorker.OutputMessage
import com.jetbrains.rider.model.debuggerWorker.OutputSubject
import com.jetbrains.rider.model.debuggerWorker.OutputType
import com.jetbrains.rider.run.IDebuggerOutputListener
import com.jetbrains.rider.util.idea.ILifetimedComponent
import com.jetbrains.rider.util.idea.LifetimedComponent
import com.intellij.openapi.diagnostic.Logger

class DebuggerOutputListener(val project: Project) : ILifetimedComponent by LifetimedComponent(project), IDebuggerOutputListener
{
    private val logger = Logger.getInstance(DebuggerOutputListener::class.java)

    override fun onOutputMessageAvailable(message: OutputMessage) {
        logger.info(message.output)

        if (message.subject == OutputSubject.ConnectionError)
        {
            val text = "Check \"Editor Attaching\" in Unity settings\n";
            XDebuggerManagerImpl.NOTIFICATION_GROUP.createNotification(text, NotificationType.ERROR).notify(project)

            var debuggerManager = project.getComponent(XDebuggerManager::class.java)
            val debugProcess = debuggerManager.currentSession!!.debugProcess as DotNetDebugProcess
            val message1 = OutputMessage(text, message.type, OutputSubject.Default)
            val console = debugProcess.console
            (console as? ConsoleView)?.print(message1.output, when (message1.type) {
                OutputType.Info -> ConsoleViewContentType.NORMAL_OUTPUT
                OutputType.Warning -> ConsoleViewContentType.LOG_WARNING_OUTPUT
                OutputType.Error -> ConsoleViewContentType.ERROR_OUTPUT
            })
        }
        super.onOutputMessageAvailable(message)
    }
}

