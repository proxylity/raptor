build-PacketIngestorLambda:
	dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/Function $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
