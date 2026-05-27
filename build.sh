#!/usr/bin/env bash
/opt/homebrew/opt/dotnet/bin/dotnet build LibreHardwareMonitorService/LibreHardwareMonitorService.csproj -c Release -p:Platform=x64 -p:EnableWindowsTargeting=true 
/opt/homebrew/opt/dotnet/bin/dotnet publish LibreHardwareMonitorService/LibreHardwareMonitorService.csproj -c Release -p:Platform=x64 -p:EnableWindowsTargeting=true
