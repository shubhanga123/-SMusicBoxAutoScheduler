# S-Music Box Auto Scheduler

A Windows desktop application for managing and scheduling YouTube video uploads.

## Features

- YouTube account authentication
- Local video ingestion
- Thumbnail management
- Scheduled publishing
- Upload queue management
- Background processing
- Retry and recovery safeguards
- SQLite database

## Requirements

- Windows 10/11
- .NET 10 SDK for development
- YouTube Data API v3 access

## Build

dotnet restore

dotnet build -c Release

## Security

OAuth authorization tokens and user databases must remain local.
Never publish personal authorization tokens or private user data.

## License

This project is licensed under the MIT License. See LICENSE.

