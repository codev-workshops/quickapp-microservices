FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5002

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Services/Customer/Customer.API/Customer.API.csproj", "Services/Customer/Customer.API/"]
COPY ["Services/Customer/Customer.Domain/Customer.Domain.csproj", "Services/Customer/Customer.Domain/"]
COPY ["Services/Customer/Customer.Infrastructure/Customer.Infrastructure.csproj", "Services/Customer/Customer.Infrastructure/"]
COPY ["Shared/Shared.Contracts/Shared.Contracts.csproj", "Shared/Shared.Contracts/"]
COPY ["Shared/Shared.Infrastructure/Shared.Infrastructure.csproj", "Shared/Shared.Infrastructure/"]
RUN dotnet restore "Services/Customer/Customer.API/Customer.API.csproj"
COPY . .
WORKDIR "/src/Services/Customer/Customer.API"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5002
ENTRYPOINT ["dotnet", "Customer.API.dll"]
