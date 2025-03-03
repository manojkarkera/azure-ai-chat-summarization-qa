import { Component, OnInit } from '@angular/core';
import { ChatDocService } from 'src/services/chat.service';

@Component({
  selector: 'app-upload-document',
  templateUrl: './upload-document.component.html',
  styleUrls: ['./upload-document.component.scss']
})
export class UploadDocumentComponent implements OnInit {

  requestType: 'chat' | 'summarize' | 'ask' = 'chat'; // Default to Chat
  userInput: string = '';
  documentContent: string = '';
  aiResponse: string = '';
  selectedFile: File | null = null;
  requestOptions = [
    { label: 'Chat', value: 'chat' },
    { label: 'Summarization', value: 'summarize' },
    { label: 'Q&A', value: 'ask' }
  ];

  constructor(private chatDocService: ChatDocService) {}

  ngOnInit(): void {
  }


  // Handle file selection
  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.selectedFile = input.files[0];
    }
  }

  sendRequest() {
    if (this.requestType === 'chat') {
      // For chat, just send text
      this.chatDocService.sendTextRequest(this.requestType, this.userInput).subscribe({
        next: (response) => {
          this.aiResponse = response.response;
        },
        error: (error) => {
          console.error('Error:', error);
          this.aiResponse = 'Error processing request.';
        },
      });
    } else if (this.selectedFile) {
      // For file-based requests, send the file
      this.chatDocService.sendFileRequest(this.requestType, this.selectedFile).subscribe({
        next: (response) => {
          this.aiResponse = response.response;
        },
        error: (error) => {
          console.error('Error:', error);
          this.aiResponse = 'Error processing request.';
        },
      });
    }
  }

}
