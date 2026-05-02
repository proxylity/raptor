build-BlockCompleterLambda:
	dotnet publish raptorq-block-completer/Function.csproj -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/Function $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap

build-CompletionEvaluatorLambda:
	dotnet publish raptorq-completion-evaluator/Function.csproj -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/Function $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
	
build-EncoderLambda:
	dotnet publish raptorq-encoder-lambda/Function.csproj -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/Function $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap

build-PacketIngestorLambda:
	dotnet publish raptorq-packet-ingestor/Function.csproj -c Release -r linux-x64 -p:PublishAot=true -o ./publish
	mv ./publish/Function $(ARTIFACTS_DIR)/bootstrap
	chmod +x $(ARTIFACTS_DIR)/bootstrap
