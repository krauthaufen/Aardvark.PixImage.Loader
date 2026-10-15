#!/bin/sh
dotnet tool restore
dotnet paket restore
dotnet build -c Release Aardvark.PixImage.PixCodec.slnx
